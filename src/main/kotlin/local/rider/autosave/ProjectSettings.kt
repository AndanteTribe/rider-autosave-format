package local.rider.autosave

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.ToggleAction
import com.intellij.openapi.components.PersistentStateComponent
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import com.intellij.openapi.components.StoragePathMacros
import com.intellij.openapi.components.service

@Service(Service.Level.PROJECT)
@State(name = "NativeAutosaveFormatting", storages = [Storage(StoragePathMacros.WORKSPACE_FILE)])
class ProjectSettings : PersistentStateComponent<ProjectSettings.Options> {
    data class Options(var enabled: Boolean = false)
    private var options = Options()
    override fun getState() = options
    override fun loadState(state: Options) { options = state }
    val enabled get() = options.enabled
    fun setEnabled(value: Boolean) { options.enabled = value }
}

class ToggleFormattingAction : ToggleAction() {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT
    override fun isSelected(e: AnActionEvent) = e.project?.service<ProjectSettings>()?.enabled == true
    override fun setSelected(e: AnActionEvent, state: Boolean) {
        e.project?.service<ProjectSettings>()?.setEnabled(state)
    }
    override fun update(e: AnActionEvent) {
        super.update(e)
        e.presentation.isEnabled = e.project != null
    }
}
