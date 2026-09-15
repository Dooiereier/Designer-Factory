using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace DesignerBackgroundTest.EditorTools
{
    // One-off setup for the imported Mixamo Drood character(s) under Assets/Models/
    // Drood/ - configures the FBX import settings (Humanoid rig, texture extraction),
    // builds a minimal Idle/Walk Animator Controller (shared by every character,
    // since Humanoid retargeting works across differently-rigged skeletons and the
    // controller only ever references clips, not a specific avatar), and saves a
    // ready-to-use prefab per character. All steps that are normally done by hand in
    // the Inspector, automated here so they're reproducible if the source files ever
    // get re-imported or replaced.
    //
    // Tools > Designer Factory > Setup Drood Character sets up the first character
    // (DroodCharacter.fbx -> DroodPrefab.prefab) plus the shared Walk/Idle animator.
    // Tools > Designer Factory > Setup Second/Third Drood Character sets up an
    // additional character (DroodCharacter2/3.fbx -> DroodPrefab2/3.prefab) reusing
    // that same animator - run the first item at least once before the others.
    public static class DroodSetupEditor
    {
        private const string WalkPath = "Assets/Models/Drood/DroodWalk.fbx";
        private const string IdlePath = "Assets/Models/Drood/DroodIdle.fbx";
        private const string RunBackwardPath = "Assets/Models/Drood/DroodRunBackward.fbx";
        private const string ControllerPath = "Assets/Models/Drood/DroodAnimator.controller";

        private const string CharacterPath = "Assets/Models/Drood/DroodCharacter.fbx";
        private const string PrefabPath = "Assets/Models/Drood/DroodPrefab.prefab";

        private const string Character2Path = "Assets/Models/Drood/DroodCharacter2.fbx";
        private const string Prefab2Path = "Assets/Models/Drood/DroodPrefab2.prefab";

        private const string Character3Path = "Assets/Models/Drood/DroodCharacter3.fbx";
        private const string Prefab3Path = "Assets/Models/Drood/DroodPrefab3.prefab";

        [MenuItem("Tools/Designer Factory/Setup Drood Character")]
        public static void Setup()
        {
            AnimatorController controller = SetupSharedAnimator();
            if (controller == null)
            {
                return;
            }
            SetupCharacter(CharacterPath, PrefabPath, controller);
        }

        [MenuItem("Tools/Designer Factory/Setup Second Drood Character")]
        public static void SetupSecondCharacter()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                Debug.LogError("[DroodSetup] " + ControllerPath + " doesn't exist yet - run \"Setup Drood Character\" first.");
                return;
            }
            SetupCharacter(Character2Path, Prefab2Path, controller);
        }

        [MenuItem("Tools/Designer Factory/Setup Third Drood Character")]
        public static void SetupThirdCharacter()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                Debug.LogError("[DroodSetup] " + ControllerPath + " doesn't exist yet - run \"Setup Drood Character\" first.");
                return;
            }
            SetupCharacter(Character3Path, Prefab3Path, controller);
        }

        // Builds (or rebuilds) the Idle/Walk/RunBackward Animator Controller shared
        // by every Drood character, returning it - or null if the source FBX files
        // aren't in place yet.
        private static AnimatorController SetupSharedAnimator()
        {
            ConfigureAnimationImport(WalkPath);
            ConfigureAnimationImport(IdlePath);
            ConfigureAnimationImport(RunBackwardPath);

            AnimationClip walkClip = LoadClip(WalkPath);
            AnimationClip idleClip = LoadClip(IdlePath);
            AnimationClip runBackwardClip = LoadClip(RunBackwardPath);
            if (walkClip == null || idleClip == null || runBackwardClip == null)
            {
                Debug.LogError("[DroodSetup] Could not find an AnimationClip inside the walk/idle/run-backward FBX after import.");
                return null;
            }

            return BuildController(idleClip, walkClip, runBackwardClip);
        }

        private static void SetupCharacter(string characterPath, string prefabPath, AnimatorController controller)
        {
            ConfigureCharacterImport(characterPath);
            Avatar avatar = LoadAvatar(characterPath);
            if (avatar == null)
            {
                Debug.LogError("[DroodSetup] Could not find/create a Humanoid Avatar on " + characterPath + " - check the FBX actually contains a valid Mixamo skeleton.");
                return;
            }

            GameObject prefab = BuildPrefab(characterPath, prefabPath, controller);
            if (prefab != null)
            {
                Debug.Log("[DroodSetup] Done - prefab saved at " + prefabPath);
            }
        }

        private static void ConfigureCharacterImport(string characterPath)
        {
            ModelImporter importer = AssetImporter.GetAtPath(characterPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError("[DroodSetup] Character FBX not found at " + characterPath);
                return;
            }
            // Reset first, then reimport, before rebuilding the avatar - if a
            // different character was previously imported at this same path (same
            // GUID, new content, e.g. swapping Ch08 for Ch17), Unity can otherwise
            // reuse the OLD cached bone-name mapping instead of re-detecting fresh
            // from this file's actual hierarchy, and fail with something like
            // "Transform 'mixamorig7:Hips' for human bone 'Hips' not found".
            // avatarSetup can only be NoAvatar while animationType isn't Human, so
            // animationType has to drop first too.
            importer.animationType = ModelImporterAnimationType.None;
            importer.avatarSetup = ModelImporterAvatarSetup.NoAvatar;
            importer.SaveAndReimport();

            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;

            // Mixamo characters embed their diffuse/normal/spec textures inside the
            // FBX rather than shipping them as loose files, but Unity's default
            // "keep embedded" material import mode doesn't reliably wire the diffuse
            // map to the generated material's Albedo slot for this kind of file - it
            // ends up with a normal map (so the mesh shows surface detail) but no
            // color. Explicitly extracting to real texture assets alongside the FBX
            // (the scripted equivalent of the Inspector's "Extract Textures" button)
            // forces Unity to redo that linking properly.
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            importer.SaveAndReimport();

            // Extracts the embedded PNGs to real texture assets and re-links them
            // onto the generated materials - the actual fix for the missing color;
            // materials themselves stay embedded in the model, which is fine. Each
            // character gets its own textures subfolder so multiple characters'
            // extracted PNGs don't collide.
            string baseName = System.IO.Path.GetFileNameWithoutExtension(characterPath);
            string textureDir = System.IO.Path.GetDirectoryName(characterPath) + "/" + baseName + ".Textures";
            importer.ExtractTextures(textureDir);
            importer.SaveAndReimport();

            MarkNormalMapTextures(textureDir);

            // Unity's auto-link from ImportViaMaterialDescription only recognizes the
            // classic Mixamo Phong-style diffuse property. Characters exported with a
            // Spec/Gloss PBR workflow instead (separate Diffuse/Glossiness/Specular
            // maps, sometimes split across multiple UDIM-tile materials like this
            // one's "Ch33_1001"/"Ch33_1002") can come through with no Albedo texture
            // at all even though the PNGs extracted fine - so wire each material's
            // main texture to its matching "*_Diffuse" file explicitly instead of
            // trusting the auto-link.
            AssignDiffuseTextures(characterPath, textureDir);
        }

        private static void AssignDiffuseTextures(string characterPath, string textureDir)
        {
            if (!AssetDatabase.IsValidFolder(textureDir))
            {
                return;
            }

            Material[] materials = AssetDatabase.LoadAllAssetsAtPath(characterPath).OfType<Material>().ToArray();
            if (materials.Length == 0)
            {
                return;
            }

            // Key each extracted "*_Diffuse.*" texture by the filename portion before
            // "_Diffuse" (e.g. "Ch33_1001_Diffuse.png" -> "Ch33_1001") so it can be
            // matched back to the material it belongs to.
            List<(string key, Texture2D texture)> diffuseTextures = new List<(string, Texture2D)>();
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { textureDir }))
            {
                string texturePath = AssetDatabase.GUIDToAssetPath(guid);
                string fileName = System.IO.Path.GetFileNameWithoutExtension(texturePath);
                int diffuseIndex = fileName.IndexOf("_Diffuse", System.StringComparison.OrdinalIgnoreCase);
                if (diffuseIndex < 0)
                {
                    continue;
                }
                Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                if (texture != null)
                {
                    diffuseTextures.Add((fileName.Substring(0, diffuseIndex), texture));
                }
            }

            if (diffuseTextures.Count == 0)
            {
                return;
            }

            bool changed = false;
            foreach (Material material in materials)
            {
                // Already has a real (non-normal-map) texture wired up - leave it alone.
                Texture existing = material.mainTexture;
                if (existing != null && existing.name.IndexOf("_Normal", System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                // Single-diffuse characters (one material, one texture) always match
                // regardless of naming; multi-material ones match by name similarity
                // in either direction (material "Ch33_1001" <-> texture key
                // "Ch33_1001").
                Texture2D match = diffuseTextures.Count == 1
                    ? diffuseTextures[0].texture
                    : diffuseTextures.FirstOrDefault(d =>
                        d.key.IndexOf(material.name, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        material.name.IndexOf(d.key, System.StringComparison.OrdinalIgnoreCase) >= 0).texture;

                if (match != null)
                {
                    material.mainTexture = match;
                    EditorUtility.SetDirty(material);
                    changed = true;
                }
                else
                {
                    Debug.LogWarning("[DroodSetup] Could not find a matching diffuse texture for material '" + material.name + "' on " + characterPath);
                }
            }

            if (changed)
            {
                AssetDatabase.SaveAssets();
            }
        }

        // Unity flags any extracted texture a material references as a normal map
        // with a "NormalMap settings" popup ("must be marked as a normal map in the
        // import settings") that otherwise needs a manual "Fix now" click per
        // character. Mixamo's own naming convention (*_Normal.png) makes this safe to
        // just do directly instead of relying on catching that dialog every time.
        private static void MarkNormalMapTextures(string textureDir)
        {
            if (!AssetDatabase.IsValidFolder(textureDir))
            {
                return;
            }
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { textureDir }))
            {
                string texturePath = AssetDatabase.GUIDToAssetPath(guid);
                if (texturePath.IndexOf("_Normal", System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                TextureImporter textureImporter = AssetImporter.GetAtPath(texturePath) as TextureImporter;
                if (textureImporter != null && textureImporter.textureType != TextureImporterType.NormalMap)
                {
                    textureImporter.textureType = TextureImporterType.NormalMap;
                    textureImporter.SaveAndReimport();
                }
            }
        }

        // Mixamo gives each separately-downloaded animation file its own bone-name
        // prefix (mixamorig, mixamorig1, mixamorig7, ...) depending on how many
        // characters were previewed in that browser session. CopyFromOther turned out
        // NOT to handle that - it literally copies the source avatar's bone-name
        // mapping and requires those exact transform names to exist in this file's
        // hierarchy, which fails the moment the prefix differs ("Transform
        // 'mixamorig:Hips' ... not found"). CreateFromThisModel instead lets each
        // file build its own avatar from its own (correctly-prefixed) hierarchy -
        // Unity's actual Humanoid retargeting happens at the Animator/muscle level
        // during playback, not by sharing one avatar object at import time, so this
        // still drives the character correctly once both are marked Humanoid.
        private static void ConfigureAnimationImport(string path)
        {
            ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError("[DroodSetup] FBX not found at " + path);
                return;
            }
            // Same reset-before-reimport reasoning as ConfigureCharacterImport - keeps
            // this safe to re-run if the walk/idle source files are ever swapped too.
            // avatarSetup can only be NoAvatar while animationType isn't Human. Also
            // clears clipAnimations first - a stale entry left over from a previous
            // successful run (referencing a takeName like "mixamo.com") can otherwise
            // make even THIS reset reimport fail with "Split Animation Take Not
            // Found", since Unity tries to validate the stored clip list against
            // whatever state the importer is currently in.
            importer.clipAnimations = new ModelImporterClipAnimation[0];
            importer.animationType = ModelImporterAnimationType.None;
            importer.avatarSetup = ModelImporterAvatarSetup.NoAvatar;
            importer.SaveAndReimport();

            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            // Commit the Human-mode settings via a reimport BEFORE reading
            // defaultClipAnimations below - otherwise it can reflect the take info
            // from the still-stale None/NoAvatar state rather than this file's actual
            // current take (Mixamo sometimes names it "mixamo.com" rather than "Take
            // 001"), causing a "Split Animation Take Not Found" error.
            importer.SaveAndReimport();

            ModelImporterClipAnimation[] clips = importer.defaultClipAnimations;
            for (int i = 0; i < clips.Length; i++)
            {
                clips[i].loopTime = true;
            }
            importer.clipAnimations = clips;

            importer.SaveAndReimport();
        }

        private static Avatar LoadAvatar(string path)
        {
            return AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
        }

        private static AnimationClip LoadClip(string path)
        {
            return AssetDatabase.LoadAllAssetsAtPath(path)
                .OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.Contains("__preview__"));
        }

        private static AnimatorController BuildController(AnimationClip idle, AnimationClip walk, AnimationClip runBackward)
        {
            // Deletes any leftover controller from a previous run first -
            // CreateAnimatorControllerAtPath doesn't cleanly overwrite an existing
            // asset at the same path (it can end up creating "DroodAnimator 1.
            // controller" instead), and this script is meant to be safely
            // re-runnable. Prefabs are deliberately NOT deleted here - they're
            // registered by GUID in ModData.asset's Prefabs list, and
            // PrefabUtility.SaveAsPrefabAsset overwrites an existing prefab file in
            // place (preserving its GUID) rather than needing a fresh one.
            AssetDatabase.DeleteAsset(ControllerPath);

            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            controller.AddParameter("IsWalking", AnimatorControllerParameterType.Bool);
            controller.AddParameter("IsFleeing", AnimatorControllerParameterType.Bool);

            AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
            AnimatorState idleState = stateMachine.AddState("Idle");
            idleState.motion = idle;
            AnimatorState walkState = stateMachine.AddState("Walk");
            walkState.motion = walk;
            AnimatorState runBackwardState = stateMachine.AddState("RunBackward");
            runBackwardState.motion = runBackward;
            stateMachine.defaultState = idleState;

            // Normal Idle<->Walk transitions - gated on IsFleeing being false so they
            // can't fire at the same time as the "Any State -> RunBackward" override
            // below and leave the state machine in an ambiguous spot.
            AnimatorStateTransition toWalk = idleState.AddTransition(walkState);
            toWalk.hasExitTime = false;
            toWalk.duration = 0.15f;
            toWalk.AddCondition(AnimatorConditionMode.If, 0f, "IsWalking");
            toWalk.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsFleeing");

            AnimatorStateTransition toIdle = walkState.AddTransition(idleState);
            toIdle.hasExitTime = false;
            toIdle.duration = 0.15f;
            toIdle.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsWalking");
            toIdle.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsFleeing");

            // IsFleeing overrides whichever state is current (Idle or Walk) from
            // anywhere, and releases back to whichever of Idle/Walk is appropriate
            // once it clears.
            AnimatorStateTransition toRunBackward = stateMachine.AddAnyStateTransition(runBackwardState);
            toRunBackward.hasExitTime = false;
            toRunBackward.duration = 0.1f;
            toRunBackward.canTransitionToSelf = false;
            toRunBackward.AddCondition(AnimatorConditionMode.If, 0f, "IsFleeing");

            AnimatorStateTransition runBackwardToWalk = runBackwardState.AddTransition(walkState);
            runBackwardToWalk.hasExitTime = false;
            runBackwardToWalk.duration = 0.15f;
            runBackwardToWalk.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsFleeing");
            runBackwardToWalk.AddCondition(AnimatorConditionMode.If, 0f, "IsWalking");

            AnimatorStateTransition runBackwardToIdle = runBackwardState.AddTransition(idleState);
            runBackwardToIdle.hasExitTime = false;
            runBackwardToIdle.duration = 0.15f;
            runBackwardToIdle.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsFleeing");
            runBackwardToIdle.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsWalking");

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return controller;
        }

        private static GameObject BuildPrefab(string characterPath, string prefabPath, AnimatorController controller)
        {
            GameObject characterAsset = AssetDatabase.LoadAssetAtPath<GameObject>(characterPath);
            if (characterAsset == null)
            {
                Debug.LogError("[DroodSetup] Could not load the character model at " + characterPath);
                return null;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(characterAsset);
            Animator animator = instance.GetComponent<Animator>();
            if (animator == null)
            {
                animator = instance.AddComponent<Animator>();
            }
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;

            GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            Object.DestroyImmediate(instance);
            return savedPrefab;
        }
    }
}
