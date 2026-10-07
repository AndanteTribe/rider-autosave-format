package local.rider.autosave

import org.junit.jupiter.api.Test

/** Run every standalone regression scenario as part of ordinary Gradle check. */
class RegressionHarnessTest {
    @Test
    fun productionSaveGateRegressionScenarios() {
        SaveGateStandaloneTest.main(emptyArray())
    }

    @Test
    fun serviceSchedulingModelScenarios() {
        ServiceSchedulingModelTest.main(emptyArray())
    }
}
