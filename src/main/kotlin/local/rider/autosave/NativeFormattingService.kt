package local.rider.autosave

import com.intellij.analysis.AnalysisScope
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.EDT
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.service
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.editor.Document
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.DumbService
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.psi.PsiDocumentManager
import com.jetbrains.rider.actions.impl.cleanup.BackendCodeCleanupService
import com.jetbrains.rider.actions.impl.cleanup.CleanupInvocationType
import com.jetbrains.rider.actions.impl.cleanup.RiderCodeCleanupUtils
import com.jetbrains.rider.model.DefaultProfileType
import com.jetbrains.rider.model.Success
import com.jetbrains.rider.settings.codeCleanup.model.createRdInspectionProfile
import java.util.WeakHashMap
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

@Service(Service.Level.PROJECT)
class NativeFormattingService(private val project: Project, private val coroutineScope: CoroutineScope) {
    private val gates = WeakHashMap<Document, SaveGate>()
    private val mutex = Mutex()
    private val log = Logger.getInstance(NativeFormattingService::class.java)

    private fun eligible(file: VirtualFile): Boolean =
        !project.isDisposed && project.service<ProjectSettings>().enabled &&
            !DumbService.isDumb(project) && SaveEligibility.owner(file) === project

    fun onSaved(document: Document, file: VirtualFile, savedStamp: Long) {
        if (!eligible(file)) return
        val manager = FileDocumentManager.getInstance()
        // Deferred callbacks may describe an older save. Never format newer unsaved typing.
        if (manager.isDocumentUnsaved(document)) return
        val currentStamp = document.modificationStamp
        if (currentStamp != savedStamp) {
            log.debug("Coalescing a saved revision for ${file.path}")
        }
        val gate = gates.getOrPut(document) { SaveGate() }
        if (!gate.begin(currentStamp)) return
        coroutineScope.launch(Dispatchers.EDT) {
            var stableAndSaved = false
            var retryPending = true
            try {
                mutex.withLock {
                    repeat(MAX_STABILIZATION_PASSES) {
                        if (!eligible(file) || manager.isDocumentUnsaved(document)) return@withLock
                        val backend = BackendCodeCleanupService.getInstance(project)
                        // Never fall back to Silent/Full Cleanup, which can change more than formatting.
                        val descriptor = backend.getCodeCleanupProfiles(false)
                            .singleOrNull { it.defaultProfileType == DefaultProfileType.REFORMAT }
                        if (descriptor == null) {
                            log.warn("Built-in Reformat Code profile is unavailable; skipping ${file.path}")
                            return@withLock
                        }
                        // Profile lookup and mutex acquisition can suspend. Recheck the entire scope.
                        if (!eligible(file) || manager.isDocumentUnsaved(document)) return@withLock
                        gate.beginPass()
                        val startStamp = document.modificationStamp
                        PsiDocumentManager.getInstance(project).commitAllDocuments()
                        val result = RiderCodeCleanupUtils.cleanupCode(
                            project, AnalysisScope(project, listOf(file)), createRdInspectionProfile(descriptor),
                            CleanupInvocationType.OnSave, false
                        )
                        if (result !is Success) {
                            log.warn("Native auto-save formatting did not succeed for ${file.path}: $result")
                            return@withLock
                        }
                        if (!eligible(file)) return@withLock
                        InternalSave.save(document)
                        // saveDocument returns void and can be vetoed or fail without throwing.
                        if (manager.isDocumentUnsaved(document)) {
                            log.warn("Formatted document remains unsaved; leaving ordinary save retry active: ${file.path}")
                            return@withLock
                        }
                        val endStamp = document.modificationStamp
                        if (!gate.needsAnotherPass(startStamp, endStamp)) {
                            stableAndSaved = true
                            return@withLock
                        }
                        // A changed stamp may be formatting OR typing after the backend snapshot.
                        // Only a later no-change pass proves the latest saved revision is stable.
                    }
                    retryPending = false
                    log.warn("Formatting did not stabilize after $MAX_STABILIZATION_PASSES passes; waiting for a new save: ${file.path}")
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                log.warn("Native auto-save formatting failed for ${file.path}; ordinary save was not blocked", e)
            } finally {
                val pendingStamp = gate.finish(document.modificationStamp, stableAndSaved)
                // A real save arriving during a failed pass must not disappear with that pass.
                if (retryPending && pendingStamp != null && !project.isDisposed) {
                    ApplicationManager.getApplication().invokeLater {
                        if (!project.isDisposed) onSaved(document, file, pendingStamp)
                    }
                }
            }
        }
    }

    private companion object {
        const val MAX_STABILIZATION_PASSES = 3
    }
}
