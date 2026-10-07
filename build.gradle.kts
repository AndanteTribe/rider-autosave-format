import groovy.json.JsonSlurper
import org.gradle.api.tasks.bundling.AbstractArchiveTask
import org.jetbrains.intellij.platform.gradle.tasks.VerifyPluginTask
import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    kotlin("jvm") version "2.3.0"
    id("org.jetbrains.intellij.platform") version "2.19.0"
}

group = "io.github.andantetribe"

val sinceRiderBuild = providers.gradleProperty("pluginSinceBuild").get()
val untilRiderBuild = providers.gradleProperty("pluginUntilBuild").get()
val targetRiderBuild = providers.gradleProperty("riderBuild")
    .orElse(providers.environmentVariable("RIDER_BUILD"))
    .getOrElse(sinceRiderBuild)
@Suppress("UNCHECKED_CAST")
val targets = JsonSlurper().parse(file("scripts/rider-targets.json")) as Map<String, Map<String, String>>
require(targets.values.any { it["build"] == targetRiderBuild }) {
    "Unlisted SDK $targetRiderBuild. Review and update scripts/rider-targets.json before adding support."
}
val riderHome = providers.gradleProperty("riderHome")
    .orElse(providers.environmentVariable("RIDER_HOME"))
    .orNull ?: error("Set -PriderHome=<Rider directory> or RIDER_HOME.")
val riderDirectory = file(riderHome)
require(riderDirectory.resolve("product-info.json").isFile) { "Rider product-info.json is missing: $riderDirectory" }
val actualRiderBuild = riderDirectory.resolve("build.txt").readText().trim().removePrefix("RD-")
val productInfo = JsonSlurper().parse(riderDirectory.resolve("product-info.json")) as Map<*, *>
require(actualRiderBuild == targetRiderBuild && productInfo["buildNumber"] == targetRiderBuild && productInfo["productCode"] == "RD") {
    "SDK is ${productInfo["productCode"]}-$actualRiderBuild, target is RD-$targetRiderBuild. Select the matching SDK."
}

val pluginVersion = providers.gradleProperty("pluginVersion").get()
require(Regex("(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)").matches(pluginVersion)) {
    "pluginVersion must be a plain major.minor.patch release version."
}
version = pluginVersion

repositories {
    mavenCentral()
    intellijPlatform { defaultRepositories() }
}

dependencies {
    intellijPlatform {
        local(riderDirectory)
        pluginVerifier("1.410")
    }
    testImplementation(kotlin("test"))
    testImplementation("org.junit.jupiter:junit-jupiter:5.11.4")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_21)
        allWarningsAsErrors.set(true)
    }
}
java {
    toolchain { languageVersion.set(JavaLanguageVersion.of(25)) }
    sourceCompatibility = JavaVersion.VERSION_21
    targetCompatibility = JavaVersion.VERSION_21
}
tasks.withType<org.jetbrains.kotlin.gradle.tasks.KotlinJvmCompile>().configureEach {
    compilerOptions.jvmTarget.set(JvmTarget.JVM_21)
}
tasks.test { useJUnitPlatform() }

intellijPlatform {
    // The plugin has no forms or searchable settings.
    instrumentCode = false
    buildSearchableOptions = false
    pluginConfiguration {
        id = providers.gradleProperty("pluginId")
        name = providers.gradleProperty("pluginName")
        version = project.version.toString()
        vendor {
            name = providers.gradleProperty("pluginVendor")
            url = providers.gradleProperty("pluginVendorUrl")
            providers.gradleProperty("pluginVendorEmail").orNull?.takeIf { it.isNotBlank() }?.let { email = it }
        }
        ideaVersion {
            sinceBuild = sinceRiderBuild
            untilBuild = untilRiderBuild
        }
    }
    publishing {
        token = providers.environmentVariable("PUBLISH_TOKEN")
        channels = providers.gradleProperty("marketplaceChannel").map { listOf(it) }.orElse(listOf("default"))
    }
    pluginVerification {
        // Local targets avoid resolving a different IDE or downloading it twice.
        ides { local(riderDirectory) }
        providers.gradleProperty("pluginVerifierPath").orNull?.let { cliPath = file(it) }
        failureLevel = VerifyPluginTask.FailureLevel.ALL
    }
}

val preparePluginDescriptor by tasks.registering(Copy::class) {
    from("src/main/resources/META-INF/plugin.xml")
    into(layout.buildDirectory.dir("generated/pluginDescriptor"))
    inputs.property("untilRiderBuild", untilRiderBuild)
    expand("untilRiderBuild" to untilRiderBuild)
}
tasks.patchPluginXml {
    dependsOn(preparePluginDescriptor)
    // The Gradle 2.x DSL does not expose strict-until-build; expand it before patching.
    inputFile = layout.buildDirectory.file("generated/pluginDescriptor/plugin.xml")
}
tasks.processResources {
    from("LICENSE") { into("META-INF") }
}

tasks.withType<AbstractArchiveTask>().configureEach {
    isPreserveFileTimestamps = false
    isReproducibleFileOrder = true
}
tasks.buildPlugin {
    dependsOn(tasks.test)
    doFirst {
        require(targetRiderBuild == sinceRiderBuild) {
            "Build the shared plugin with the minimum supported SDK: $sinceRiderBuild."
        }
    }
}
// All required modules are bundled in the exact local Rider SDK.
tasks.verifyPlugin {
    offline.set(true)
    // CI verifies the same downloaded artifact against every SDK without rebuilding it.
    providers.gradleProperty("verificationArchive").orNull?.let { archiveFile.set(file(it)) }
}

val dotnet = providers.gradleProperty("dotnetExecutable").getOrElse("dotnet")
val buildRepositoryTools by tasks.registering(Exec::class) {
    group = "build"
    description = "Build the C# repository tool once before package and metadata verification."
    workingDir(rootDir)
    commandLine(dotnet, "build", "scripts/repository-tools.cs", "--configuration", "Release")
}
val repositoryTests by tasks.registering(Exec::class) {
    group = "verification"
    description = "Test the repository's SDK and release-check helpers."
    workingDir(rootDir)
    commandLine(dotnet, "run", "--file", "scripts/test-repository-tools.cs", "--configuration", "Release")
}
tasks.check { dependsOn(repositoryTests) }

val verifyPackage by tasks.registering(Exec::class) {
    group = "verification"
    description = "Check the exact built ZIP, metadata, bytecode and bundled files."
    dependsOn(tasks.buildPlugin, buildRepositoryTools)
    workingDir(rootDir)
    commandLine(dotnet, "run", "--file", "scripts/repository-tools.cs",
        "--configuration", "Release", "--no-build", "--no-restore", "--",
        "verify-plugin-zip", tasks.buildPlugin.get().archiveFile.get().asFile, targetRiderBuild)
}
val verifyReleaseMetadata by tasks.registering(Exec::class) {
    group = "verification"
    description = "Require complete public source metadata before a release."
    dependsOn(buildRepositoryTools)
    workingDir(rootDir)
    commandLine(dotnet, "run", "--file", "scripts/repository-tools.cs",
        "--configuration", "Release", "--no-build", "--no-restore", "--", "check-release-metadata")
}
val releaseCheck by tasks.registering {
    group = "verification"
    description = "Check a release candidate. Does not publish or replace manual IDE tests."
    dependsOn(tasks.check, verifyPackage, tasks.verifyPlugin, verifyReleaseMetadata)
}

// First registration is manual. Later uploads require an explicit local/CI opt-in.
// The ordinary push/PR workflow never receives a Marketplace token.
tasks.publishPlugin {
    dependsOn(releaseCheck)
    doFirst {
        require(providers.gradleProperty("allowMarketplacePublish").orNull == "true") {
            "Publishing requires -PallowMarketplacePublish=true."
        }
        require(!providers.environmentVariable("PUBLISH_TOKEN").orNull.isNullOrBlank()) {
            "PUBLISH_TOKEN must be set only for the authorized publishing step."
        }
    }
}
