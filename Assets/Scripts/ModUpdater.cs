using System;
using System.Collections;
using System.Text.RegularExpressions;
using ModApi.Ui;
using UnityEngine;
using UnityEngine.Networking;

namespace DesignerBackgroundTest
{
    // Checks GitHub for a newer version than what's actually installed (Mod.Instance.
    // ModInfo.Version, set by the game itself from the loaded mod's manifest before
    // OnModInitialized runs - no KnownMods-list timing dance needed) and, if one
    // exists, points the player at the mod's SimpleRockets.com listing to grab it.
    // The version source and the download destination are deliberately different:
    // ModData.asset's _versionMajor/_versionMinor on the main branch is bumped at
    // release time alongside the code, while players actually download the packaged
    // .sr2-mod from the SimpleRockets.com listing, not from GitHub.
    public static class ModUpdater
    {
        private const string ModDisplayName = "Designer Factory";
        private const string VersionCheckUrl = "https://raw.githubusercontent.com/Dooiereier/Designer-Factory/main/Assets/ModData.asset";
        private const string DownloadPageUrl = "https://www.simplerockets.com/Mods/View/353069/Designer-Factory";

        public static void CheckForUpdate()
        {
            GameObject host = new GameObject("DesignerFactoryModUpdater");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<Runner>().StartCoroutine(CheckForUpdateCoroutine(host));
        }

        private static IEnumerator CheckForUpdateCoroutine(GameObject host)
        {
            try
            {
                Version localVersion = Assets.Scripts.Mod.Instance.ModInfo?.Version;
                if (localVersion == null)
                {
                    yield break;
                }

                using (UnityWebRequest request = UnityWebRequest.Get(VersionCheckUrl))
                {
                    yield return request.SendWebRequest();

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogWarning($"[DesignerBackgroundTest] Update check failed: {request.error}");
                        yield break;
                    }

                    Version remoteVersion = ParseVersion(request.downloadHandler.text);
                    if (remoteVersion == null || remoteVersion.CompareTo(localVersion) <= 0)
                    {
                        yield break;
                    }

                    ShowUpdateDialog(localVersion, remoteVersion);
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(host);
            }
        }

        private static Version ParseVersion(string modDataAssetText)
        {
            Match majorMatch = Regex.Match(modDataAssetText, @"_versionMajor:\s*(\d+)");
            Match minorMatch = Regex.Match(modDataAssetText, @"_versionMinor:\s*(\d+)");
            if (!majorMatch.Success || !minorMatch.Success)
            {
                return null;
            }
            return new Version(int.Parse(majorMatch.Groups[1].Value), int.Parse(minorMatch.Groups[1].Value));
        }

        private static void ShowUpdateDialog(Version localVersion, Version remoteVersion)
        {
            MessageDialogScript dialog = Assets.Scripts.Game.Instance.UserInterface.CreateMessageDialog(
                $"A new version of {ModDisplayName} is available.\n\nInstalled: {localVersion}\nLatest: {remoteVersion}",
                () => Application.OpenURL(DownloadPageUrl));
            dialog.OkayButtonText = "Open Download Page";
        }

        // Bare MonoBehaviour host for the coroutine - GameMod (Mod.Instance's base
        // class) isn't one itself, so it can't call StartCoroutine directly.
        private class Runner : MonoBehaviour
        {
        }
    }
}
