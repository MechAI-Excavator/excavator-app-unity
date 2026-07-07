#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public sealed class WebResourcesBuildSync : IPreprocessBuildWithReport
{
    private const string SourcePath = "Assets/WebResouces";
    private const string TargetPath = "Assets/StreamingAssets/WebResouces";

    public int callbackOrder => -100;

    [MenuItem("Tools/Excavator/Sync WebResouces To StreamingAssets")]
    public static void SyncNow()
    {
        SyncDirectory(SourcePath, TargetPath);
        AssetDatabase.Refresh();
        Debug.Log("[WebResourcesBuildSync] Synced WebResouces to StreamingAssets.");
    }

    public void OnPreprocessBuild(BuildReport report)
    {
        SyncNow();
    }

    private static void SyncDirectory(string sourcePath, string targetPath)
    {
        if (!Directory.Exists(sourcePath))
        {
            Debug.LogWarning($"[WebResourcesBuildSync] Source directory not found: {sourcePath}");
            return;
        }

        if (Directory.Exists(targetPath))
            Directory.Delete(targetPath, true);

        Directory.CreateDirectory(targetPath);
        CopyDirectory(sourcePath, targetPath);
    }

    private static void CopyDirectory(string sourcePath, string targetPath)
    {
        foreach (string directory in Directory.GetDirectories(sourcePath))
        {
            string name = Path.GetFileName(directory);
            if (name == ".git") continue;

            string childTarget = Path.Combine(targetPath, name);
            Directory.CreateDirectory(childTarget);
            CopyDirectory(directory, childTarget);
        }

        foreach (string file in Directory.GetFiles(sourcePath))
        {
            string extension = Path.GetExtension(file);
            string name = Path.GetFileName(file);
            if (extension == ".meta" || name == ".DS_Store") continue;

            File.Copy(file, Path.Combine(targetPath, name), true);
        }
    }
}
#endif
