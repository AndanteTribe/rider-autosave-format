package local.rider.autosave

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ReadAction
import com.intellij.openapi.components.service
import com.intellij.openapi.editor.Document
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.fileEditor.FileDocumentManagerListener
import com.intellij.openapi.project.ProjectManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.roots.ProjectFileIndex
import com.intellij.openapi.util.Key
import com.intellij.openapi.vfs.VirtualFile

/** Checked again after each suspension; a queued file can move or change owners. */
internal object SaveEligibility {
    fun owner(file: VirtualFile): Project? = ReadAction.computeBlocking<Project?, RuntimeException> {
        if (!file.isValid || !file.isWritable || !file.isInLocalFileSystem ||
            !file.extension.equals("cs", ignoreCase = true)) return@computeBlocking null
        ProjectManager.getInstance().openProjects.filter {
            !it.isDisposed && ProjectFileIndex.getInstance(it).isInContent(file)
        }.singleOrNull()
    }
}

/** The event is synchronous, so suppress only our own resave before invokeLater. */
internal object InternalSave {
    private val key = Key.create<Boolean>("local.rider.autosave.internalSave")
    fun isActive(document: Document) = document.getUserData(key) == true

    fun save(document: Document) {
        val previous = document.getUserData(key)
        document.putUserData(key, true)
        try {
            FileDocumentManager.getInstance().saveDocument(document)
        } finally {
            document.putUserData(key, previous)
        }
    }
}

class SaveListener : FileDocumentManagerListener {
    override fun beforeDocumentSaving(document: Document) {
        if (InternalSave.isActive(document)) return
        val savedStamp = document.modificationStamp
        // Never wait for the ReSharper backend inside a save/write operation.
        ApplicationManager.getApplication().invokeLater {
            val file = FileDocumentManager.getInstance().getFile(document) ?: return@invokeLater
            val project = SaveEligibility.owner(file) ?: return@invokeLater
            if (!project.service<ProjectSettings>().enabled) return@invokeLater
            project.service<NativeFormattingService>().onSaved(document, file, savedStamp)
        }
    }
}
