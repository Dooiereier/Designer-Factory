using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DesignerBackgroundTest
{
    [HarmonyPatch(typeof(Assets.Scripts.Design.DesignerCameraScript), "Initialize")]
    public static class DesignerBackgroundTestPatch
    {
        // Flip to false before publishing to ship a "locked" hangar - the F2
        // structure editor (add/move/delete/save/load) becomes fully inaccessible to
        // players, while whatever's already saved in PlacedStructures.json still
        // gets loaded and shown (see StructureEditController.EditingEnabled).
        private const bool EnableStructureEditor = false;

        // Minimums - the hangar is sized around the actual loaded craft's bounds (see
        // BuildHangar), but never smaller than this, so a tiny craft still gets a
        // reasonably spacious-looking hangar.
        private const float MinHalfWidth = 25f;   // X
        private const float MinHalfDepth = 25f;   // Z
        private const float MinHeight = 50f;      // Y
        private const float WallThickness = 1f;
        private const int TrussCount = 5;

        // Fallback if the real platform/craft can't be found for some reason.
        private const float FallbackFloorTopY = -11.352f;

        // Tracks the currently-built shell so a later rebuild (on CraftLoaded) can
        // remove the old one first instead of stacking duplicates.
        private static GameObject _hangarRoot;

        // Latest known designer/camera pair, read by RefreshHangar() below - kept
        // as static state (rather than captured in a closure at Postfix time)
        // because the native "Refresh Hangar" toolbar button (see
        // DesignerFactoryUi.cs) is wired up once via the UserInterface's own
        // rebuild-action system and needs to reach whichever designer/camera are
        // CURRENT whenever it's actually clicked, not whichever were current the
        // moment the button was created.
        private static Assets.Scripts.Design.DesignerScript _lastDesignerScript;
        private static Camera _lastCam;

        // Called by the native "Refresh Hangar" toolbar button's click handler.
        public static void RefreshHangar()
        {
            if (_lastDesignerScript == null || _lastCam == null)
            {
                Debug.LogWarning("[DesignerBackgroundTest] Refresh Hangar clicked before the Designer was ready - nothing to refresh yet.");
                return;
            }
            Debug.Log("[DesignerBackgroundTest] Manual hangar refresh (toolbar button).");
            BuildHangar(_lastDesignerScript, _lastCam);
        }

        static void Postfix(Assets.Scripts.Design.DesignerCameraScript __instance, Assets.Scripts.Design.DesignerScript designerScript)
        {
            Camera cam = __instance.Camera;
            if (cam == null)
            {
                Debug.LogError("[DesignerBackgroundTest] Camera was null in postfix!");
                return;
            }
            _lastDesignerScript = designerScript;
            _lastCam = cam;

            Debug.Log($"[DesignerBackgroundTest] Patch firing. Current clearFlags: {cam.clearFlags}, backgroundColor: {cam.backgroundColor}");

            // Step 1 (confirmed working: clean solid magenta, no compositor bleed) - superseded below.
            // cam.clearFlags = CameraClearFlags.SolidColor;
            // cam.backgroundColor = Color.magenta;

            // Step 2 (confirmed working: clean procedural sky, no compositor bleed) - superseded below.
            // cam.clearFlags = CameraClearFlags.Skybox;
            // sb.material = new Material(Shader.Find("Skybox/Procedural"));

            // Step 4 (confirmed working: real hangar-interior panorama via a pre-baked
            // Material + ResourceLoader.LoadAsset, sidestepping runtime shader-stripping)
            // - abandoned: flat panorama has no parallax, doesn't read as a real space.
            // See git history / conversation for the full ResourceLoader/_otherAssets
            // wiring if this needs to come back.

            // Step 5: real 3D hangar shell built from plain Unity primitives - no asset
            // import, no AssetBundle wiring, no shader lookups. Floor/walls/roof/trusses
            // are all GameObject.CreatePrimitive() at runtime; open on the +Z ("door")
            // side. These have no PartScript/Collider-on-a-part relationship to the
            // craft/Assembly system at all, so unlike the earlier part-reuse experiment
            // there is no risk of the designer's part-connection system picking them up.
            // Floor/wall textures are generated procedurally at runtime for the same
            // reason - no image asset means no repeat of the ResourceLoader/shader-
            // stripping fight from Step 4.
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.75f, 0.85f, 0.95f); // pale sky, visible through the open door

            // Distance fog so the road/ground fade out smoothly rather than hitting a
            // hard edge at the world boundary - fog color matches the sky so it blends
            // rather than showing as a visible haze wall.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = cam.backgroundColor;
            RenderSettings.fogStartDistance = 120f;
            RenderSettings.fogEndDistance = 650f;

            HideDesignerPlatform(designerScript);

            // Ground-based walk camera (F to toggle) - added once here; FloorY gets kept
            // in sync with whatever hangar is currently built inside BuildHangar below.
            WalkCameraController walkCamera = cam.gameObject.GetComponent<WalkCameraController>();
            if (walkCamera == null)
            {
                walkCamera = cam.gameObject.AddComponent<WalkCameraController>();
            }
            walkCamera.DesignerCameraScriptRef = __instance;

            // Lightweight add/delete/move/rotate/scale editor (T to toggle the panel,
            // then G/R/S to transform whichever placed structure is selected) for
            // imported Planet Studio structures - added once here just like the walk
            // camera itself. Structures are parented under a standalone root created
            // here (not under _hangarRoot, which gets destroyed and rebuilt on every
            // craft load) so a placement survives switching craft.
            StructureEditController structureEditor = cam.gameObject.GetComponent<StructureEditController>();
            bool isNewStructureEditor = structureEditor == null;
            if (isNewStructureEditor)
            {
                structureEditor = cam.gameObject.AddComponent<StructureEditController>();
                GameObject structuresRoot = new GameObject("DesignerFactory_PlacedStructures");
                structureEditor.SpawnParent = structuresRoot.transform;
                // Confirmed working via an earlier probe: stock celestial bodies build
                // their KSC buildings from prefabs at plain paths like "Flight/
                // GameView/Structures/Hangar1" (see Earth's own CelestialDatabase
                // XML), and those paths are Resources-loadable from here. This full
                // list is every distinct "Flight/GameView/Structures/*" prefabPath
                // found across ALL stock celestial body XML files (not just Earth),
                // so it includes things Earth alone didn't use - roads/taxiways,
                // cranes, railings, etc.
                structureEditor.CatalogPaths = new[]
                {
                    "Flight/GameView/Structures/Barrel",
                    "Flight/GameView/Structures/BaseCubePrimitive",
                    "Flight/GameView/Structures/BaseCylinderHollowPrimitive",
                    "Flight/GameView/Structures/BaseCylinderPrimitive",
                    "Flight/GameView/Structures/BaseSpherePrimitive",
                    "Flight/GameView/Structures/BoxWooden",
                    "Flight/GameView/Structures/Cladh",
                    "Flight/GameView/Structures/Crane",
                    "Flight/GameView/Structures/DesertBaseRunway",
                    "Flight/GameView/Structures/Door1",
                    "Flight/GameView/Structures/Door2",
                    "Flight/GameView/Structures/Door3",
                    "Flight/GameView/Structures/DoorGarage",
                    "Flight/GameView/Structures/DoorGarageOpen",
                    "Flight/GameView/Structures/DroneShip",
                    "Flight/GameView/Structures/FuelTankCylinder",
                    "Flight/GameView/Structures/FuelTankLarge",
                    "Flight/GameView/Structures/FuelTankRound",
                    "Flight/GameView/Structures/Graffiti",
                    "Flight/GameView/Structures/Hangar1",
                    "Flight/GameView/Structures/Hangar2",
                    "Flight/GameView/Structures/Hangar3",
                    "Flight/GameView/Structures/Hangar4",
                    "Flight/GameView/Structures/Hangar5",
                    "Flight/GameView/Structures/HangarBunker",
                    "Flight/GameView/Structures/HangarPad",
                    "Flight/GameView/Structures/HangarReinforcedLarge",
                    "Flight/GameView/Structures/HangarReinforcedSmall",
                    "Flight/GameView/Structures/HangarStairs",
                    "Flight/GameView/Structures/HangarTaxiway",
                    "Flight/GameView/Structures/HeliPad",
                    "Flight/GameView/Structures/LaunchFX",
                    "Flight/GameView/Structures/LaunchFXLight",
                    "Flight/GameView/Structures/LaunchFXPad",
                    "Flight/GameView/Structures/LaunchFXTrench",
                    "Flight/GameView/Structures/LaunchPadLarge",
                    "Flight/GameView/Structures/LaunchPadLargeTaxiWay",
                    "Flight/GameView/Structures/LaunchPadRaised",
                    "Flight/GameView/Structures/LaunchPadSmall",
                    "Flight/GameView/Structures/LaunchPadSmallTaxiWay",
                    "Flight/GameView/Structures/LightPointLight",
                    "Flight/GameView/Structures/LightPortable",
                    "Flight/GameView/Structures/LightSpotlight",
                    "Flight/GameView/Structures/Message",
                    "Flight/GameView/Structures/MoonBase",
                    "Flight/GameView/Structures/MoonBaseHangar",
                    "Flight/GameView/Structures/OutpostLarge",
                    "Flight/GameView/Structures/OutpostSmall",
                    "Flight/GameView/Structures/Pallet",
                    "Flight/GameView/Structures/PipeCorner",
                    "Flight/GameView/Structures/PipeStraight",
                    "Flight/GameView/Structures/PrimaryLaunchSite",
                    "Flight/GameView/Structures/PrimaryRunway",
                    "Flight/GameView/Structures/Railing45",
                    "Flight/GameView/Structures/RailingCorner",
                    "Flight/GameView/Structures/RailingEnd",
                    "Flight/GameView/Structures/RailingPost",
                    "Flight/GameView/Structures/RailingSpanse",
                    "Flight/GameView/Structures/Runway",
                    "Flight/GameView/Structures/RunwayLarge",
                    "Flight/GameView/Structures/RunwayLights",
                    "Flight/GameView/Structures/RunwayRamp",
                    "Flight/GameView/Structures/RunwayRoad",
                    "Flight/GameView/Structures/SR2logo",
                    "Flight/GameView/Structures/SatelliteDish",
                    "Flight/GameView/Structures/Sculpture",
                    "Flight/GameView/Structures/ShippingCrateClosed",
                    "Flight/GameView/Structures/ShippingCrateOpen",
                    "Flight/GameView/Structures/StairsBasic",
                    "Flight/GameView/Structures/StrutAngle",
                    "Flight/GameView/Structures/StrutLong",
                    "Flight/GameView/Structures/StrutShort",
                    "Flight/GameView/Structures/StrutsTower",
                    "Flight/GameView/Structures/TowerATC",
                    "Flight/GameView/Structures/TowerATCBasic",
                    "Flight/GameView/Structures/VAB",
                    "Flight/GameView/Structures/VillageTerrace2",
                    "Flight/GameView/Structures/VillageTerrace3",
                    "Flight/GameView/Structures/WaterTower",
                    "Flight/GameView/Structures/WaterTowerTall",
                    "Flight/GameView/Structures/WindowLarge",
                    "Flight/GameView/Structures/WindowMedium",
                    "Flight/GameView/Structures/WindowSmall",
                };
                // User-imported props (from Poly Pizza), built by
                // Assets/Editor/PropSetupEditor.cs into Assets/Models/Props/.
                structureEditor.CustomCatalogPaths = new string[]
                {
                    "Assets/Models/Props/Couch_Medium1.prefab",
                    "Assets/Models/Props/Houseplant_5.prefab",
                    "Assets/Models/Props/StandingDesk.prefab",
                    "Assets/Models/Props/vendo.prefab",
                    "Assets/Models/Props/sofa.prefab",
                    "Assets/Models/Props/Wall_Art_Classical_01.prefab",
                    "Assets/Models/Props/Wall_Painting_01.prefab",
                    "Assets/Models/Props/painting1.prefab",
                    "Assets/Models/Props/ForkliftTruck.prefab",
                    "Assets/Models/Props/HandCrane.prefab",
                    "Assets/Models/Props/WoodCrate.prefab",
                    "Assets/Models/Props/ShippingContainer.prefab",
                };
            }
            structureEditor.WalkCamera = walkCamera;
            structureEditor.EditingEnabled = EnableStructureEditor;
            walkCamera.StructureEditor = structureEditor;

            // Step 6: size/position the shell around whatever craft is actually loaded,
            // and rebuild it every time a different craft is loaded - a fixed-size shell
            // (the original Step 5) looked fine for the small test capsule but a much
            // larger craft (e.g. a multi-stage rocket whose root part sits partway up the
            // stack, not at its base) simply didn't fit and clipped through the floor.
            BuildHangar(designerScript, cam);
            if (isNewStructureEditor)
            {
                // Restore whatever was saved last time (see StructureEditController.
                // SaveConfig/LoadConfig) - safe to call with no save file present yet.
                // Deliberately called AFTER the first BuildHangar (not before, like it
                // used to be) - BuildHangar's own SyncFloorY/SyncWallAnchors calls run
                // first and establish the CURRENT real floor height, which LoadConfig
                // needs already known to correctly shift structures from their saved
                // (possibly stale, from a different floor height) position instead of
                // loading them floating or sunk.
                structureEditor.LoadConfig();

                // The first BuildHangar above ran before any structures were loaded,
                // so anything keyed off StructureEditController.GetPlacedPositions()
                // (billboard tree exclusion, Drood wander-target avoidance) had
                // nothing to avoid yet - trees/Droods from that first pass have no
                // idea the just-loaded structures exist. Rebuilding once more now
                // that they're loaded fixes that without needing a manual refresh.
                BuildHangar(designerScript, cam);
            }
            designerScript.CraftLoaded += () => BuildHangar(designerScript, cam);

            // Manual on-demand resize (F3) for when a craft grows mid-edit rather
            // than via a fresh load - deliberately not automatic on every structural
            // edit, since that would mean a full destroy-and-rebuild of the hangar
            // shell on every part add/remove/resize.
            HangarRefreshTrigger refreshTrigger = cam.gameObject.GetComponent<HangarRefreshTrigger>();
            if (refreshTrigger == null)
            {
                refreshTrigger = cam.gameObject.AddComponent<HangarRefreshTrigger>();
            }
            refreshTrigger.RefreshAction = () => BuildHangar(designerScript, cam);
        }

        // Minimal key-press trigger for the manual hangar refresh (F3) - lives on the
        // camera GameObject alongside WalkCameraController/StructureEditController so
        // it keeps running regardless of walk/orbit mode (rebuilding doesn't involve
        // any mouse/camera input, so there's no conflict either way).
        private class HangarRefreshTrigger : MonoBehaviour
        {
            public System.Action RefreshAction;
            public KeyCode RefreshKey = KeyCode.F3;

            void Update()
            {
                if (RefreshAction != null && Input.GetKeyDown(RefreshKey))
                {
                    Debug.Log("[DesignerBackgroundTest] Manual hangar refresh (F3).");
                    RefreshAction();
                }
            }
        }

        private static void BuildHangar(Assets.Scripts.Design.DesignerScript designerScript, Camera cam)
        {
            if (_hangarRoot != null)
            {
                Object.Destroy(_hangarRoot);
            }

            Bounds craftBounds = default;
            bool haveBounds = false;
            ModApi.Craft.ICraftScript craftScript = designerScript?.CraftScript;
            if (craftScript != null)
            {
                craftBounds = craftScript.CalculateBounds(true);
                haveBounds = craftBounds.size.sqrMagnitude > 0.0001f;
            }

            float FloorTopY = haveBounds ? craftBounds.min.y : FallbackFloorTopY;
            float HalfWidth = haveBounds ? Mathf.Max(MinHalfWidth, craftBounds.extents.x * 1.8f + 10f) : MinHalfWidth;
            float HalfDepth = haveBounds ? Mathf.Max(MinHalfDepth, craftBounds.extents.z * 1.8f + 15f) : MinHalfDepth;
            float Height = haveBounds ? Mathf.Max(MinHeight, craftBounds.size.y * 1.3f) : MinHeight;

            Debug.Log($"[DesignerBackgroundTest] Building hangar. haveBounds={haveBounds} craftBounds={craftBounds} -> HalfWidth={HalfWidth:F1} HalfDepth={HalfDepth:F1} Height={Height:F1} FloorTopY={FloorTopY:F1}");

            WalkCameraController walkCamera = cam.gameObject.GetComponent<WalkCameraController>();
            if (walkCamera != null)
            {
                walkCamera.FloorY = FloorTopY;
            }

            GameObject hangarRoot = new GameObject("DesignerFactory_HangarShell");
            _hangarRoot = hangarRoot;

            Color structureColor = new Color(0.62f, 0.63f, 0.65f);   // walls/roof - light metal
            Color trussColor = new Color(0.32f, 0.33f, 0.35f);       // trusses/columns - darker steel
            Color skylightColor = new Color(0.92f, 0.95f, 1f);       // bright, suggests light coming through

            Texture2D floorTexture = CreateFloorTexture();
            // Tint rather than a texture edit, so every concrete surface (interior
            // floor, access road, launch pad) shares the exact same source texture and
            // stays in sync - halves the rendered brightness (the material multiplies
            // texture color by this) without touching the generated pixel data itself.
            Color concreteTint = new Color(0.5f, 0.5f, 0.5f);
            Texture2D wallTexture = CreateWallTexture();
            Texture2D grassTexture = CreateGrassTexture();
            Texture2D hazardTexture = CreateHazardStripeTexture();
            Texture2D windowWallTexture = CreateWindowWallTexture();

            // Outside ground, sitting just below the indoor floor's top surface so the
            // two don't z-fight where the indoor floor covers it, and visible everywhere
            // beyond the hangar footprint - especially through the open door.
            const float outsideGroundSize = 800f;
            const float outsideGroundStep = 0.05f;
            const float outsideGroundThickness = 0.1f;
            float outsideGroundTopY = FloorTopY - outsideGroundStep;
            CreateBox(hangarRoot.transform, "OutsideGround",
                new Vector3(0f, outsideGroundTopY - outsideGroundThickness * 0.5f, 0f),
                new Vector3(outsideGroundSize, outsideGroundThickness, outsideGroundSize),
                Color.white, grassTexture, new Vector2(outsideGroundSize / 4f, outsideGroundSize / 4f));

            // Floor
            CreateBox(hangarRoot.transform, "Floor",
                new Vector3(0f, FloorTopY - WallThickness * 0.5f, 0f),
                new Vector3(HalfWidth * 2f, WallThickness, HalfDepth * 2f),
                concreteTint, floorTexture, new Vector2(HalfWidth * 2f / 5f, HalfDepth * 2f / 5f));

            // Back wall (-Z)
            CreateBox(hangarRoot.transform, "BackWall",
                new Vector3(0f, FloorTopY + Height * 0.5f, -HalfDepth),
                new Vector3(HalfWidth * 2f, Height, WallThickness),
                Color.white, wallTexture, new Vector2(HalfWidth * 2f / 2f, Height / 2f));

            // Left wall (-X)
            CreateBox(hangarRoot.transform, "LeftWall",
                new Vector3(-HalfWidth, FloorTopY + Height * 0.5f, 0f),
                new Vector3(WallThickness, Height, HalfDepth * 2f),
                Color.white, wallTexture, new Vector2(HalfDepth * 2f / 2f, Height / 2f));

            // Right wall (+X) - same plain wall material as the left wall (opposite
            // side from the canteen) rather than the glass-paned window texture, per
            // explicit request. Still split into two segments with a real gap between
            // them - this is the doorway connecting to the cafeteria annex built
            // further down.
            const float cafeteriaDoorWidth = 4f;
            float cafeteriaZStart = -HalfDepth;
            float cafeteriaDoorCenterZ = cafeteriaZStart + 8f;
            float cafeteriaDoorMinZ = cafeteriaDoorCenterZ - cafeteriaDoorWidth * 0.5f;
            float cafeteriaDoorMaxZ = cafeteriaDoorCenterZ + cafeteriaDoorWidth * 0.5f;

            float rightWallBackLength = cafeteriaDoorMinZ - (-HalfDepth);
            CreateBox(hangarRoot.transform, "RightWallBack",
                new Vector3(HalfWidth, FloorTopY + Height * 0.5f, -HalfDepth + rightWallBackLength * 0.5f),
                new Vector3(WallThickness, Height, rightWallBackLength),
                Color.white, wallTexture, new Vector2(rightWallBackLength / 4f, Height / 4f));

            float rightWallFrontLength = HalfDepth - cafeteriaDoorMaxZ;
            CreateBox(hangarRoot.transform, "RightWallFront",
                new Vector3(HalfWidth, FloorTopY + Height * 0.5f, cafeteriaDoorMaxZ + rightWallFrontLength * 0.5f),
                new Vector3(WallThickness, Height, rightWallFrontLength),
                Color.white, wallTexture, new Vector2(rightWallFrontLength / 4f, Height / 4f));

            // Header above the doorway - without this, the gap between the two wall
            // segments above ran all the way to the ceiling instead of stopping at a
            // normal door height.
            const float cafeteriaDoorHeight = 5f;
            float doorHeaderHeight = Height - cafeteriaDoorHeight;
            CreateBox(hangarRoot.transform, "RightWallHeader",
                new Vector3(HalfWidth, FloorTopY + cafeteriaDoorHeight + doorHeaderHeight * 0.5f, cafeteriaDoorCenterZ),
                new Vector3(WallThickness, doorHeaderHeight, cafeteriaDoorWidth),
                Color.white, wallTexture, new Vector2(cafeteriaDoorWidth / 4f, doorHeaderHeight / 4f));

            // Roof
            CreateBox(hangarRoot.transform, "Roof",
                new Vector3(0f, FloorTopY + Height + WallThickness * 0.5f, 0f),
                new Vector3(HalfWidth * 2f, WallThickness, HalfDepth * 2f),
                structureColor);

            // Roof trusses - repeating ribs along Z, each spanning the width at roof height -
            // and a matching support column at each end, wall to truss, for real structure.
            for (int i = 0; i < TrussCount; i++)
            {
                float t = TrussCount == 1 ? 0.5f : (float)i / (TrussCount - 1);
                float z = Mathf.Lerp(-HalfDepth + 2f, HalfDepth - 2f, t);
                float trussY = FloorTopY + Height - 1.5f;

                CreateBox(hangarRoot.transform, $"Truss_{i}",
                    new Vector3(0f, trussY, z),
                    new Vector3(HalfWidth * 2f - 1f, 0.6f, 0.6f),
                    trussColor);

                CreateBox(hangarRoot.transform, $"ColumnLeft_{i}",
                    new Vector3(-HalfWidth + 0.75f, FloorTopY + (trussY - FloorTopY) * 0.5f, z),
                    new Vector3(0.6f, trussY - FloorTopY, 0.6f),
                    trussColor);

                CreateBox(hangarRoot.transform, $"ColumnRight_{i}",
                    new Vector3(HalfWidth - 0.75f, FloorTopY + (trussY - FloorTopY) * 0.5f, z),
                    new Vector3(0.6f, trussY - FloorTopY, 0.6f),
                    trussColor);

                // Hazard-striped base, slightly wider than the column so it reads as a
                // wrapped safety marking rather than z-fighting with the column itself.
                const float baseHeight = 2f;
                CreateBox(hangarRoot.transform, $"ColumnBaseLeft_{i}",
                    new Vector3(-HalfWidth + 0.75f, FloorTopY + baseHeight * 0.5f, z),
                    new Vector3(0.7f, baseHeight, 0.7f),
                    Color.white, hazardTexture, new Vector2(1f, 2f));
                CreateBox(hangarRoot.transform, $"ColumnBaseRight_{i}",
                    new Vector3(HalfWidth - 0.75f, FloorTopY + baseHeight * 0.5f, z),
                    new Vector3(0.7f, baseHeight, 0.7f),
                    Color.white, hazardTexture, new Vector2(1f, 2f));
            }

            // Diagonal X-cross bracing between adjacent columns, left and right walls -
            // the classic industrial-scaffold look from the reference photos.
            for (int i = 0; i < TrussCount - 1; i++)
            {
                float t0 = TrussCount == 1 ? 0.5f : (float)i / (TrussCount - 1);
                float t1 = TrussCount == 1 ? 0.5f : (float)(i + 1) / (TrussCount - 1);
                float z0 = Mathf.Lerp(-HalfDepth + 2f, HalfDepth - 2f, t0);
                float z1 = Mathf.Lerp(-HalfDepth + 2f, HalfDepth - 2f, t1);
                float trussY = FloorTopY + Height - 1.5f;
                const float braceThickness = 0.2f;

                CreateBoxBetween(hangarRoot.transform, $"BraceLeftA_{i}",
                    new Vector3(-HalfWidth + 0.75f, FloorTopY, z0), new Vector3(-HalfWidth + 0.75f, trussY, z1),
                    braceThickness, trussColor);
                CreateBoxBetween(hangarRoot.transform, $"BraceLeftB_{i}",
                    new Vector3(-HalfWidth + 0.75f, trussY, z0), new Vector3(-HalfWidth + 0.75f, FloorTopY, z1),
                    braceThickness, trussColor);
                CreateBoxBetween(hangarRoot.transform, $"BraceRightA_{i}",
                    new Vector3(HalfWidth - 0.75f, FloorTopY, z0), new Vector3(HalfWidth - 0.75f, trussY, z1),
                    braceThickness, trussColor);
                CreateBoxBetween(hangarRoot.transform, $"BraceRightB_{i}",
                    new Vector3(HalfWidth - 0.75f, trussY, z0), new Vector3(HalfWidth - 0.75f, FloorTopY, z1),
                    braceThickness, trussColor);
            }

            // Skylight strips along the roof ridge, between the truss lines.
            for (int i = 0; i < TrussCount - 1; i++)
            {
                float t0 = (float)i / (TrussCount - 1);
                float t1 = (float)(i + 1) / (TrussCount - 1);
                float z = Mathf.Lerp(-HalfDepth + 2f, HalfDepth - 2f, (t0 + t1) * 0.5f);
                CreateBox(hangarRoot.transform, $"Skylight_{i}",
                    new Vector3(0f, FloorTopY + Height + WallThickness * 0.5f + 0.05f, z),
                    new Vector3(HalfWidth * 2f - 4f, 0.05f, 1.2f),
                    skylightColor);
            }

            // Wall-mounted utility pipes along the back wall, colour-coded like real
            // industrial pipe runs (fuel/coolant/pneumatic).
            float pipeZ = -HalfDepth + 0.8f;
            float pipeSpanX = HalfWidth * 2f - 6f;
            CreateCylinder(hangarRoot.transform, "PipeRed",
                new Vector3(0f, FloorTopY + Height * 0.24f, pipeZ), Quaternion.Euler(0f, 0f, 90f),
                0.5f, pipeSpanX, new Color(0.55f, 0.22f, 0.15f));
            CreateCylinder(hangarRoot.transform, "PipeBlue",
                new Vector3(0f, FloorTopY + Height * 0.30f, pipeZ), Quaternion.Euler(0f, 0f, 90f),
                0.4f, pipeSpanX, new Color(0.2f, 0.32f, 0.55f));
            CreateCylinder(hangarRoot.transform, "PipeGrey",
                new Vector3(0f, FloorTopY + Height * 0.36f, pipeZ), Quaternion.Euler(0f, 0f, 90f),
                0.6f, pipeSpanX, new Color(0.5f, 0.5f, 0.5f));

            // Mezzanine balcony running around all three enclosed walls (back, left,
            // right) as one continuous loop - walkable in F1 mode via the Platforms
            // list populated below, matching this geometry exactly.
            // Fixed height above the floor rather than a fraction of Height - a real
            // mezzanine level doesn't get taller just because the building around it
            // does, and this keeps the balcony at a sensible walking-accessible height
            // regardless of how big the loaded craft is.
            const float catwalkFixedHeight = 10f;
            // Walkway depth/width tripled from the original 2.5m - inset/edge-inset
            // are derived from it (rather than separately hardcoded) so widening it
            // automatically keeps the rail at the true outer edge and the support legs
            // centred under the walkway, instead of drifting out of sync with it.
            const float catwalkWallGap = 0.6f; // gap between wall and walkway inner edge
            const float catwalkDepth = 2.5f * 3f * 0.7f; // 3x from before, then 30% less wide
            float catwalkInset = catwalkWallGap + catwalkDepth * 0.5f; // wall to walkway centre
            float catwalkEdgeInset = catwalkWallGap + catwalkDepth; // wall to walkway outer/rail edge
            float catwalkFrontZ = -HalfDepth + catwalkEdgeInset;

            // Computed here - rather than only down in the stair-building loop below -
            // so the back rail/posts can stop clear of each stair's footprint instead
            // of running straight across the top of it. Must stay in sync with the
            // matching constants used when the stairs themselves are built.
            const float stairCenterOffset = 4.3f;
            const float stairTreadWidth = 1.2f * 2.5f;
            float stairGapHalfWidth = stairTreadWidth * 0.5f + 0.3f;
            float leftStairWalkwayX = -(HalfWidth - stairCenterOffset - catwalkInset);
            float rightStairWalkwayX = HalfWidth - stairCenterOffset - catwalkInset;
            float backRailHalfLength = Mathf.Min(HalfWidth - 9f, rightStairWalkwayX - stairGapHalfWidth);

            // Side segments (left and right walls), running from the back corner out
            // toward the open door - same Z range at every level, since each level
            // stacks directly above the one below rather than shifting in plan.
            float sideZStart = -HalfDepth + catwalkEdgeInset;
            float sideZEnd = HalfDepth - 2f;
            float sideLength = sideZEnd - sideZStart;
            float sideCenterZ = sideZStart + sideLength * 0.5f;

            if (walkCamera != null)
            {
                walkCamera.Platforms.Clear();
            }

            // Keep adding another balcony level on top of the last as long as there's
            // still more than 20m of clear height left above it - a fixed one-off cap
            // would either crowd a very tall hangar (built for a very tall craft) with
            // a single towering blank wall, or under-serve a modest one, so the level
            // count is derived straight from the hangar's own Height instead. Shared
            // with the staircase loop further below so both stay in lock-step.
            // Checks the clearance the NEXT level would actually end up with (not the
            // current level's clearance) before adding it - otherwise the level that
            // triggers the loop to stop is the same one that already got added with
            // under 20m to the ceiling.
            int catwalkLevelCount = 1;
            while (Height - catwalkFixedHeight * (catwalkLevelCount + 1) > 20f)
            {
                catwalkLevelCount++;
            }

            // Mezzanine balconies running around all three enclosed walls (back, left,
            // right), each one a full loop directly above the last, walkable in F1
            // mode via the Platforms list populated below, matching this geometry
            // exactly.
            for (int level = 0; level < catwalkLevelCount; level++)
            {
                string levelSuffix = level == 0 ? "" : (level + 1).ToString();
                // Fixed spacing per level rather than a fraction of Height - a real
                // mezzanine storey doesn't get taller just because the building around
                // it does, and this keeps every level at a sensible walking-accessible
                // height regardless of how big the loaded craft is.
                float catwalkY = FloorTopY + catwalkFixedHeight * (level + 1);
                // The walkable surface directly below this level: the real floor for
                // the first level, or the previous level's own balcony deck (whose top
                // sits 0.15m above its catwalkY, same as the WalkPlatform entries
                // below) for every level after that.
                float levelFloorY = level == 0 ? FloorTopY : FloorTopY + catwalkFixedHeight * level + 0.15f;

                // Back wall segment (full width).
                CreateBox(hangarRoot.transform, "CatwalkBack" + levelSuffix,
                    new Vector3(0f, catwalkY, -HalfDepth + catwalkInset),
                    new Vector3(HalfWidth * 2f, 0.3f, catwalkDepth),
                    structureColor);
                CreateBox(hangarRoot.transform, "CatwalkBackRail" + levelSuffix,
                    new Vector3(0f, catwalkY + 0.9f, catwalkFrontZ),
                    new Vector3(backRailHalfLength * 2f, 0.1f, 0.1f),
                    trussColor);
                for (float x = -backRailHalfLength; x <= backRailHalfLength + 0.01f; x += 5f)
                {
                    CreateBox(hangarRoot.transform, $"CatwalkBackPost{levelSuffix}_{x}",
                        new Vector3(x, catwalkY + 0.45f, catwalkFrontZ),
                        new Vector3(0.1f, 0.9f, 0.1f),
                        trussColor);
                }
                // At the outer/rail edge (catwalkFrontZ), not the walkway's centre depth.
                CreateBox(hangarRoot.transform, "CatwalkLegLeft" + levelSuffix,
                    new Vector3(-HalfWidth + 3f, levelFloorY + (catwalkY - levelFloorY) * 0.5f, catwalkFrontZ),
                    new Vector3(0.5f, catwalkY - levelFloorY, 0.5f),
                    trussColor);
                CreateBox(hangarRoot.transform, "CatwalkLegRight" + levelSuffix,
                    new Vector3(HalfWidth - 3f, levelFloorY + (catwalkY - levelFloorY) * 0.5f, catwalkFrontZ),
                    new Vector3(0.5f, catwalkY - levelFloorY, 0.5f),
                    trussColor);
                if (walkCamera != null)
                {
                    walkCamera.Platforms.Add(new WalkPlatform
                    {
                        MinX = -HalfWidth, MaxX = HalfWidth,
                        MinZ = -HalfDepth + 0.6f, MaxZ = -HalfDepth + catwalkEdgeInset,
                        TopY = catwalkY + 0.15f,
                    });
                }

                foreach (float sign in new[] { -1f, 1f })
                {
                    string side = sign < 0f ? "Left" : "Right";
                    float wallX = sign * HalfWidth;
                    float walkwayX = wallX - sign * catwalkInset;
                    float railX = wallX - sign * catwalkEdgeInset;

                    CreateBox(hangarRoot.transform, $"Catwalk{side}{levelSuffix}",
                        new Vector3(walkwayX, catwalkY, sideCenterZ),
                        new Vector3(catwalkDepth, 0.3f, sideLength),
                        structureColor);
                    CreateBox(hangarRoot.transform, $"Catwalk{side}Rail{levelSuffix}",
                        new Vector3(railX, catwalkY + 0.9f, sideCenterZ),
                        new Vector3(0.1f, 0.1f, sideLength),
                        trussColor);

                    // Corner piece connecting the back rail's end to this side rail's
                    // start - without this there was a gap in the guardrail at both back
                    // corners, since the back rail stops at the walkway's own X extent
                    // while the side rail sits further in/out at railX. Uses the same
                    // backRailHalfLength the back rail itself was built to, so this stays
                    // attached to its true endpoint even as that endpoint moves to clear
                    // the stairs.
                    float backRailEndX = sign * backRailHalfLength;
                    float cornerMinX = Mathf.Min(backRailEndX, railX);
                    float cornerMaxX = Mathf.Max(backRailEndX, railX);
                    CreateBox(hangarRoot.transform, $"Catwalk{side}CornerRail{levelSuffix}",
                        new Vector3((cornerMinX + cornerMaxX) * 0.5f, catwalkY + 0.9f, catwalkFrontZ),
                        new Vector3(cornerMaxX - cornerMinX, 0.1f, 0.1f),
                        trussColor);

                    for (float z = sideZStart + 2.5f; z <= sideZEnd - 0.01f; z += 5f)
                    {
                        CreateBox(hangarRoot.transform, $"Catwalk{side}Post{levelSuffix}_{z}",
                            new Vector3(railX, catwalkY + 0.45f, z),
                            new Vector3(0.1f, 0.9f, 0.1f),
                            trussColor);
                    }
                    // At the outer/rail edge (railX), not the walkway's centre depth.
                    CreateBox(hangarRoot.transform, $"Catwalk{side}Leg{levelSuffix}",
                        new Vector3(railX, levelFloorY + (catwalkY - levelFloorY) * 0.5f, sideZEnd - 2f),
                        new Vector3(0.5f, catwalkY - levelFloorY, 0.5f),
                        trussColor);

                    // End-cap rail at the FRONT (open, +Z) end of this walkway - the
                    // long rail above only runs alongside it (protecting the inner/
                    // outer X edge), and the CornerRail above only closes the BACK
                    // corner where it meets the back catwalk. Without this, the front
                    // end of every level's side balcony was just open air - the mezzanine
                    // only wraps the three enclosed walls, so there's nothing else
                    // (no wall, no rail) stopping a walk straight off the end.
                    CreateBox(hangarRoot.transform, $"Catwalk{side}FrontRail{levelSuffix}",
                        new Vector3(walkwayX, catwalkY + 0.9f, sideZEnd),
                        new Vector3(catwalkDepth, 0.1f, 0.1f),
                        trussColor);

                    if (walkCamera != null)
                    {
                        float minX = sign < 0f ? wallX + 0.6f : railX - 0.1f;
                        float maxX = sign < 0f ? railX + 0.1f : wallX - 0.6f;
                        walkCamera.Platforms.Add(new WalkPlatform
                        {
                            MinX = minX, MaxX = maxX,
                            MinZ = sideZStart, MaxZ = sideZEnd,
                            TopY = catwalkY + 0.15f,
                        });
                    }
                }
            }

            // Overhead crane hook: the HandCrane prop already models its own cable
            // rig hanging down from its pivot, so it mounts directly to the middle
            // roof truss (same trussY/z=0 as the centre Truss_i beam below) rather
            // than to a separate hand-built gantry. The old placeholder gantry (rail
            // beams + bridge + a primitive cable box, with the hook hung part-way
            // down an extra hand-computed cable length below that) has been removed
            // entirely - it was redundant with the prop's own rig and left the hook
            // sitting far lower than the truss it should be hanging from.
            float craneTrussY = FloorTopY + Height - 1.5f;
            GameObject craneHookPrefab = Assets.Scripts.Mod.Instance.ResourceLoader.LoadAsset<GameObject>("Assets/Models/Props/HandCrane.prefab");
            if (craneHookPrefab != null)
            {
                GameObject craneHook = Object.Instantiate(craneHookPrefab, hangarRoot.transform, false);
                craneHook.name = "CraneHookBlock";
                craneHook.transform.localScale = new Vector3(25f, 33f, 33f);
                craneHook.transform.localPosition = Vector3.zero;

                // The model's own rig extends both above AND below its pivot (it's
                // not just a hook hanging from a top-anchored cable) - positioning
                // by pivot alone put a large chunk of it poking up through the roof
                // into the sky. Measuring the actual (post-scale) renderer bounds
                // and shifting so the TOP sits at the truss instead fixes that
                // regardless of where the model's own pivot happens to sit.
                Renderer[] craneRenderers = craneHook.GetComponentsInChildren<Renderer>();
                if (craneRenderers.Length > 0)
                {
                    Bounds craneBounds = craneRenderers[0].bounds;
                    for (int i = 1; i < craneRenderers.Length; i++)
                    {
                        craneBounds.Encapsulate(craneRenderers[i].bounds);
                    }
                    craneHook.transform.localPosition = new Vector3(0f, craneTrussY - craneBounds.max.y, 0f);
                }
                else
                {
                    craneHook.transform.localPosition = new Vector3(0f, craneTrussY, 0f);
                }
            }
            else
            {
                Debug.LogWarning("[DesignerBackgroundTest] Could not load HandCrane prefab - falling back to the primitive hook block.");
                CreateBox(hangarRoot.transform, "CraneHookBlock",
                    new Vector3(0f, craneTrussY - 5f, 0f), new Vector3(1f, 1f, 1f), trussColor);
            }

            // Roof turbine vents.
            float ventBaseY = FloorTopY + Height + WallThickness + 0.4f;
            foreach (float ventT in new[] { -0.4f, 0f, 0.4f })
            {
                float ventZ = ventT * HalfDepth;
                CreateBox(hangarRoot.transform, $"VentHousing_{ventZ}",
                    new Vector3(0f, ventBaseY, ventZ), new Vector3(1.5f, 0.8f, 1.5f), structureColor);
                CreateCylinder(hangarRoot.transform, $"VentTurbine_{ventZ}",
                    new Vector3(0f, ventBaseY + 0.7f, ventZ), Quaternion.identity,
                    1.2f, 0.6f, trussColor);
            }

            // Clerestory windows along the back wall, near the roofline.
            for (int i = 0; i < 6; i++)
            {
                float x = Mathf.Lerp(-HalfWidth + 4f, HalfWidth - 4f, i / 5f);
                CreateBox(hangarRoot.transform, $"ClerestoryWindow_{i}",
                    new Vector3(x, FloorTopY + Height - 4f, -HalfDepth + WallThickness * 0.5f + 0.05f),
                    new Vector3(2f, 2f, 0.1f),
                    skylightColor);
            }

            // Large bay-door-shaped panel on the left wall.
            CreateBox(hangarRoot.transform, "BayDoorPanel",
                new Vector3(-HalfWidth + WallThickness * 0.5f + 0.05f, FloorTopY + Mathf.Min(8f, Height * 0.3f), 0f),
                new Vector3(0.1f, Mathf.Min(16f, Height * 0.6f), 10f),
                new Color(0.25f, 0.25f, 0.27f));

            // Overhead work-lights, one per truss bay on each side, instead of just two.
            for (int i = 0; i < TrussCount; i++)
            {
                float t = TrussCount == 1 ? 0.5f : (float)i / (TrussCount - 1);
                float z = Mathf.Lerp(-HalfDepth + 2f, HalfDepth - 2f, t);
                CreateLight(hangarRoot.transform, new Vector3(-HalfWidth * 0.5f, FloorTopY + Height - 3f, z));
                CreateLight(hangarRoot.transform, new Vector3(HalfWidth * 0.5f, FloorTopY + Height - 3f, z));
            }

            // --- Interior clutter ---

            // Crate stack in the back-left corner.
            Color crateColorA = new Color(0.55f, 0.42f, 0.28f);
            Color crateColorB = new Color(0.45f, 0.34f, 0.22f);
            float crateCornerX = -HalfWidth + 4f;
            float crateCornerZ = -HalfDepth + 4f;
            CreateBox(hangarRoot.transform, "Crate1", new Vector3(crateCornerX, FloorTopY + 1f, crateCornerZ), new Vector3(2f, 2f, 2f), crateColorA);
            CreateBox(hangarRoot.transform, "Crate2", new Vector3(crateCornerX + 2.2f, FloorTopY + 0.75f, crateCornerZ), new Vector3(1.5f, 1.5f, 1.5f), crateColorB);
            CreateBox(hangarRoot.transform, "Crate3", new Vector3(crateCornerX, FloorTopY + 2.75f, crateCornerZ), new Vector3(1.2f, 1.2f, 1.2f), crateColorB);
            CreateBox(hangarRoot.transform, "Crate4", new Vector3(crateCornerX + 0.2f, FloorTopY + 0.4f, crateCornerZ + 2.3f), new Vector3(2.5f, 0.8f, 1.6f), crateColorA);


            // Staircases up to BOTH the left and right balcony segments (mirrored) at
            // every level, anchored so each flight's top actually lands on that
            // level's AFT (back) balcony segment rather than the side ones - the top Z
            // is fixed at the back segment's own centre line, and the base is derived
            // by extending toward the open door from there, instead of anchoring the
            // base near the door and letting the top float (which previously landed
            // the stairs on the side balcony instead). Open risers: each step is just
            // a thin tread plate rather than a solid block down to the floor, matching
            // the industrial scaffold look elsewhere in the hangar. Treads are 2.5x
            // wider and 30% steeper (70% of the "3x less steep" run) than the original
            // crammed version. Every level above the first climbs from that level's
            // own balcony deck instead of the ground floor, but otherwise reuses the
            // exact same X/Z footprint and run as the first flight, stacked directly
            // on top of it.
            // One more than the previous 21 - the lowest step (i==0 below) is always
            // the enlarged landing-sized one, so bumping this shifts that landing down
            // to a new bottom step and automatically demotes the old bottom step
            // (now i==1) to regular size, no other change needed.
            const int stairStepCount = 22;
            const float stairTreadThickness = 0.1f;

            // The old crammed placement's run was ~6m; "3x less steep" was 3x that,
            // and this is now 30% steeper than THAT (i.e. 70% of the 3x run). Same run
            // at every level, since every level stacks directly above the ground-floor
            // one rather than shifting in plan.
            const float oldCrampedRun = 6f;
            float targetRun = oldCrampedRun * 3f * 0.7f;
            // Landing point within each level's own back-balcony depth range
            // [0.6, catwalkEdgeInset] - kept close to the wall (not centred)
            // specifically to stay clear of the crate/propellant-tank clusters, which
            // occupy roughly Z=[+3, +7] from the back wall in that ground-floor corner.
            float stairTopZ = -HalfDepth + 6f;
            // Climb heads away from the door (toward the back) as it rises; clamp the
            // run so the base doesn't overshoot past the open door.
            float maxAvailableRun = Mathf.Max(8f, sideZEnd - stairTopZ - 4f);
            float totalRun = Mathf.Min(targetRun, maxAvailableRun);
            float stepRun = totalRun / stairStepCount;
            float stairBaseZ = stairTopZ + totalRun;

            for (int level = 0; level < catwalkLevelCount; level++)
            {
                string levelSuffix = level == 0 ? "" : (level + 1).ToString();
                float catwalkY = FloorTopY + catwalkFixedHeight * (level + 1);
                // Same "floor below this level" reasoning as the balcony loop above.
                float levelFloorY = level == 0 ? FloorTopY : FloorTopY + catwalkFixedHeight * level + 0.15f;
                // The flight intentionally tops out one whole riser below the
                // balcony's actual floor (balconySurfaceY) rather than landing flush
                // with it - dividing by stairStepCount + 1 (not stairStepCount) gives
                // each riser the same height a complete flight reaching the balcony
                // would use; building only stairStepCount of them just stops one
                // short. stairTopSurfaceY (used below for the handrails) comes out
                // to the actual top tread's height, one riser lower than the balcony.
                // Tread i=0 (the enlarged landing) sits flush with the level floor -
                // zero risers up, not one - so there's no visible gap between the floor
                // and the bottom step. That means only stairStepCount-1 actual risers
                // are climbed by the time the top tread is reached, one riser short of
                // the balcony as before (see stairTopSurfaceY below).
                float balconySurfaceY = catwalkY + 0.15f;
                float stepRise = (balconySurfaceY - levelFloorY) / stairStepCount;
                // Tiny sink below the floor plane so the bottom tread's top face isn't
                // exactly coplanar with the floor's top face (was causing z-fighting) -
                // applied to the whole flight so spacing between treads is unaffected.
                const float stairFloorClearance = 0.03f;
                float stairFlightBaseY = levelFloorY - stairFloorClearance;
                float stairTopSurfaceY = stairFlightBaseY + stepRise * (stairStepCount - 1);

                foreach (float stairSign in new[] { -1f, 1f })
                {
                    string stairSide = stairSign < 0f ? "Left" : "Right";
                    // Reuses the same X positions the back rail/posts were already built
                    // to clear (see leftStairWalkwayX/rightStairWalkwayX above), so the
                    // stairs and the gap left for them can never drift out of sync.
                    float stairWalkwayX = stairSign < 0f ? leftStairWalkwayX : rightStairWalkwayX;

                    for (int i = 0; i < stairStepCount; i++)
                    {
                        float stepTopY = stairFlightBaseY + stepRise * i;
                        // The lowest step of each flight is deepened 4x and extended
                        // toward the front (away from the wall, past the original
                        // stairBaseZ) rather than centred like every other tread, so
                        // it reads as a proper landing to step off onto instead of
                        // just another identical stair tread. Its back edge (the
                        // junction with the step above it) stays exactly where a
                        // normal-depth tread's would be, so the flight above it is
                        // completely unaffected.
                        bool isLowestStep = i == 0;
                        float stepDepth = isLowestStep ? stepRun * 4f : stepRun;
                        float stepBackZ = stairBaseZ - stepRun * (i + 1);
                        float stepCenterZ = stepBackZ + stepDepth * 0.5f;

                        CreateBox(hangarRoot.transform, $"Stair{stairSide}{levelSuffix}_Step_{i}",
                            new Vector3(stairWalkwayX, stepTopY - stairTreadThickness * 0.5f, stepCenterZ),
                            new Vector3(stairTreadWidth, stairTreadThickness, stepDepth + 0.02f),
                            trussColor);

                        if (walkCamera != null)
                        {
                            walkCamera.Platforms.Add(new WalkPlatform
                            {
                                MinX = stairWalkwayX - stairTreadWidth * 0.5f, MaxX = stairWalkwayX + stairTreadWidth * 0.5f,
                                MinZ = stepCenterZ - stepDepth * 0.5f, MaxZ = stepCenterZ + stepDepth * 0.5f,
                                TopY = stepTopY,
                            });
                        }
                    }

                    CreateBoxBetween(hangarRoot.transform, $"Stair{stairSide}RailInner{levelSuffix}",
                        new Vector3(stairWalkwayX - stairTreadWidth * 0.5f - 0.05f, levelFloorY + 0.9f, stairBaseZ),
                        new Vector3(stairWalkwayX - stairTreadWidth * 0.5f - 0.05f, stairTopSurfaceY + 0.9f, stairTopZ),
                        0.06f, trussColor);
                    CreateBoxBetween(hangarRoot.transform, $"Stair{stairSide}RailOuter{levelSuffix}",
                        new Vector3(stairWalkwayX + stairTreadWidth * 0.5f + 0.05f, levelFloorY + 0.9f, stairBaseZ),
                        new Vector3(stairWalkwayX + stairTreadWidth * 0.5f + 0.05f, stairTopSurfaceY + 0.9f, stairTopZ),
                        0.06f, trussColor);
                }
            }

            // Wall-mounted control panel near the back wall.
            CreateBox(hangarRoot.transform, "ControlPanel",
                new Vector3(HalfWidth - WallThickness * 0.5f - 0.1f, FloorTopY + 2f, -HalfDepth + 4f),
                new Vector3(0.15f, 1.5f, 1f), new Color(0.3f, 0.3f, 0.32f));
            CreateBox(hangarRoot.transform, "ControlPanelScreen",
                new Vector3(HalfWidth - WallThickness * 0.5f - 0.18f, FloorTopY + 2.3f, -HalfDepth + 4f),
                new Vector3(0.02f, 0.6f, 0.6f), new Color(0.3f, 0.85f, 0.9f));

            // KSP-VAB-style painted floor markings: a ring around the craft, straight
            // white guide lines out to each wall, and a hazard-striped door threshold.
            float markRadius = haveBounds ? Mathf.Max(4f, Mathf.Max(craftBounds.extents.x, craftBounds.extents.z) + 2f) : 4f;
            float markY = FloorTopY + 0.02f;
            Color lineColor = new Color(0.92f, 0.93f, 0.95f);

            // Skip the ring entirely if it'd be wider than the hangar itself (e.g. a
            // very large craft) rather than drawing one that pokes through the walls.
            bool ringFitsHangar = markRadius <= HalfWidth && markRadius <= HalfDepth;
            if (ringFitsHangar)
            {
                CreateCylinder(hangarRoot.transform, "FloorRingOuter", new Vector3(0f, markY, 0f), Quaternion.identity, markRadius * 2f, 0.01f, lineColor);
                CreateCylinder(hangarRoot.transform, "FloorRingInner", new Vector3(0f, markY + 0.002f, 0f), Quaternion.identity, markRadius * 2f - 0.4f, 0.01f, new Color(0.5f, 0.5f, 0.52f));
            }

            // Same guard as the ring above - these run from the ring's edge out to a
            // wall (HalfDepth/HalfWidth - markRadius), which goes negative-length in
            // the same oversized-craft case.
            if (ringFitsHangar)
            {
                CreateBox(hangarRoot.transform, "GuideLineBack", new Vector3(0f, markY, -(HalfDepth + markRadius) * 0.5f), new Vector3(0.25f, 0.01f, HalfDepth - markRadius), lineColor);
                CreateBox(hangarRoot.transform, "GuideLineDoor", new Vector3(0f, markY, (HalfDepth + markRadius) * 0.5f), new Vector3(0.25f, 0.01f, HalfDepth - markRadius), lineColor);
                CreateBox(hangarRoot.transform, "GuideLineLeft", new Vector3(-(HalfWidth + markRadius) * 0.5f, markY, 0f), new Vector3(HalfWidth - markRadius, 0.01f, 0.25f), lineColor);
                CreateBox(hangarRoot.transform, "GuideLineRight", new Vector3((HalfWidth + markRadius) * 0.5f, markY, 0f), new Vector3(HalfWidth - markRadius, 0.01f, 0.25f), lineColor);
            }

            // Hazard-striped threshold at the open door, spanning the full width.
            CreateBox(hangarRoot.transform, "DoorThreshold", new Vector3(0f, markY, HalfDepth - 1f), new Vector3(HalfWidth * 2f, 0.01f, 1f), Color.white, hazardTexture, new Vector2(HalfWidth, 1f));

            // KSP-VAB-floor-style vehicle lanes: solid painted yellow lanes running
            // the depth of the hangar (so their length always matches the hangar's
            // current length), mirrored on both sides of the centre ring and
            // repositioned proportionally to the hangar's current half-width (so
            // they track outward/inward as the hangar resizes with the craft, not
            // just clamped to a fixed offset).
            Color laneYellow = new Color(0.95f, 0.75f, 0.05f);
            float laneWidth = 0.375f;

            foreach (float laneSign in new[] { -1f, 1f })
            {
                float laneOffset = Mathf.Max(markRadius + laneWidth * 0.5f + 2f, HalfWidth * 0.5f);
                float laneX = laneSign * Mathf.Min(laneOffset, HalfWidth - 2f);
                CreateBox(hangarRoot.transform, $"VehicleLane_{laneSign}",
                    new Vector3(laneX, markY - 0.001f, 0f), new Vector3(laneWidth, 0.01f, HalfDepth * 2f), laneYellow);
            }

            // Unlabeled parking-bay outlines along the same wall the lane runs next
            // to - hollow rectangles, each built from 4 thin border boxes.
            const int bayCount = 3;
            float bayDepth = 4f;
            float bayWidth = 3.5f;
            float bayX = HalfWidth - bayWidth * 0.5f - 1f;
            float bayBorder = 0.1f;
            for (int b = 0; b < bayCount; b++)
            {
                float bayZ = Mathf.Lerp(-HalfDepth + 6f, HalfDepth - 10f, (float)b / Mathf.Max(1, bayCount - 1));
                string bayName = $"ParkingBay_{b}";
                CreateBox(hangarRoot.transform, bayName + "_Near", new Vector3(bayX, markY, bayZ - bayDepth * 0.5f), new Vector3(bayWidth, 0.01f, bayBorder), lineColor);
                CreateBox(hangarRoot.transform, bayName + "_Far", new Vector3(bayX, markY, bayZ + bayDepth * 0.5f), new Vector3(bayWidth, 0.01f, bayBorder), lineColor);
                CreateBox(hangarRoot.transform, bayName + "_Left", new Vector3(bayX - bayWidth * 0.5f, markY, bayZ), new Vector3(bayBorder, 0.01f, bayDepth), lineColor);
                CreateBox(hangarRoot.transform, bayName + "_Right", new Vector3(bayX + bayWidth * 0.5f, markY, bayZ), new Vector3(bayBorder, 0.01f, bayDepth), lineColor);
            }

            // --- Exterior ---

            float yardHalfWidth = HalfWidth + 15f;
            float yardHalfDepth = HalfDepth + 25f;


            // Exterior propellant storage tanks near the right wall, outside.
            CreateCylinder(hangarRoot.transform, "ExteriorTank1", new Vector3(HalfWidth + 6f, outsideGroundTopY + 4f, 0f), Quaternion.identity, 3f, 8f, new Color(0.85f, 0.86f, 0.87f));
            CreateCylinder(hangarRoot.transform, "ExteriorTank1Band", new Vector3(HalfWidth + 6f, outsideGroundTopY + 4f, 0f), Quaternion.identity, 3.05f, 0.8f, new Color(0.6f, 0.15f, 0.1f));
            CreateCylinder(hangarRoot.transform, "ExteriorTank2", new Vector3(HalfWidth + 12f, outsideGroundTopY + 3f, 0f), Quaternion.identity, 2.4f, 6f, new Color(0.85f, 0.86f, 0.87f));
            CreateCylinder(hangarRoot.transform, "ExteriorTank2Band", new Vector3(HalfWidth + 12f, outsideGroundTopY + 3f, 0f), Quaternion.identity, 2.45f, 0.7f, new Color(0.15f, 0.3f, 0.55f));

            // No real 3D trees anymore - all tree coverage is now the 2D billboard
            // band below plus the distant treeline ring. These two radii just mark
            // where that billboard band's inner edge starts (beyond the yard fence).
            float forestMinRadius = Mathf.Max(yardHalfWidth, yardHalfDepth) + 60f;
            float forestMaxRadius = forestMinRadius + 100f;

            // Shared distance for the treeline ring and the outer edge of the
            // billboard band below - pushed to just inside the edge of the outside
            // ground plane, as far as it can go while still standing on solid ground.
            float treelineRadius = outsideGroundSize * 0.5f - 20f;

            // Mid-distance billboard trees: cheap Y-axis-locked 2D tree cards filling
            // the band beyond where the player would ever walk (past the real forest)
            // and short of the treeline ring, adding apparent density without needing
            // hundreds more real (7-primitive) trees. Skipped gracefully if Sprites/
            // Default isn't available, same reasoning as the clouds/treeline.
            Shader billboardTreeShader = Shader.Find("Sprites/Default");
            if (billboardTreeShader != null)
            {
                Texture2D billboardTreeTexture = CreateBillboardTreeTexture();
                Material billboardTreeMaterial = new Material(billboardTreeShader);
                billboardTreeMaterial.mainTexture = billboardTreeTexture;

                const int billboardTreeCount = 250 * 5;
                float billboardMinRadius = forestMaxRadius + 10f;
                float billboardMaxRadius = treelineRadius - 20f;
                // Trees can start much closer directly behind the hangar (-Z, opposite
                // the launch pad) than anywhere else - halved there, fading back to the
                // normal radius toward the sides and front so there's no hard seam.
                float billboardMinRadiusBack = billboardMinRadius * 0.5f;

                // Skip any tree candidate landing too close to something placed via
                // the F2 editor (structures, waypoint markers, ...). Uses each
                // structure's actual measured footprint (not a fixed radius around
                // its pivot) plus a small margin - a fixed radius badly under-covered
                // large scaled-up structures (e.g. a placed launch pad taxiway) while
                // over-covering tiny ones like a crate.
                const float structureClearMargin = 3f;
                StructureEditController treeExclusionSource = cam.gameObject.GetComponent<StructureEditController>();
                List<Bounds> placedStructureFootprints = treeExclusionSource != null
                    ? treeExclusionSource.GetPlacedFootprints()
                    : new List<Bounds>();

                System.Random billboardRandom = new System.Random(24680);
                for (int b = 0; b < billboardTreeCount; b++)
                {
                    float angle = (float)billboardRandom.NextDouble() * Mathf.PI * 2f;
                    // 1 when facing straight back (-Z), fading to 0 by the sides/front.
                    float backFactor = Mathf.Clamp01(-Mathf.Sin(angle));
                    float localMinRadius = Mathf.Lerp(billboardMinRadius, billboardMinRadiusBack, backFactor);
                    float radius = Mathf.Lerp(localMinRadius, billboardMaxRadius, (float)billboardRandom.NextDouble());
                    float bx = Mathf.Cos(angle) * radius;
                    float bz = Mathf.Sin(angle) * radius;

                    bool tooCloseToStructure = false;
                    foreach (Bounds footprint in placedStructureFootprints)
                    {
                        if (bx >= footprint.min.x - structureClearMargin && bx <= footprint.max.x + structureClearMargin &&
                            bz >= footprint.min.z - structureClearMargin && bz <= footprint.max.z + structureClearMargin)
                        {
                            tooCloseToStructure = true;
                            break;
                        }
                    }
                    if (tooCloseToStructure)
                    {
                        continue;
                    }

                    float cardHeight = 10f + (float)billboardRandom.NextDouble() * 8f;
                    float cardWidth = cardHeight * 0.55f;

                    GameObject card = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    card.name = $"BillboardTree_{b}";
                    Collider cardCollider = card.GetComponent<Collider>();
                    if (cardCollider != null)
                    {
                        Object.Destroy(cardCollider);
                    }
                    card.transform.SetParent(hangarRoot.transform, false);
                    card.transform.localPosition = new Vector3(bx, outsideGroundTopY + cardHeight * 0.5f, bz);
                    card.transform.localScale = new Vector3(cardWidth, cardHeight, 1f);

                    Renderer cardRenderer = card.GetComponent<Renderer>();
                    cardRenderer.material = billboardTreeMaterial;
                    float tint = 0.85f + (float)billboardRandom.NextDouble() * 0.3f;
                    cardRenderer.material.color = new Color(tint, tint, tint, 1f);

                    YAxisBillboardFacer billboard = card.AddComponent<YAxisBillboardFacer>();
                    billboard.TargetCamera = cam;
                }
                Debug.Log($"[DesignerBackgroundTest] Built {billboardTreeCount} mid-distance billboard trees.");
            }
            else
            {
                Debug.LogWarning("[DesignerBackgroundTest] Sprites/Default shader not found - skipping billboard trees.");
            }

            // Distant treeline ring beyond the real trees: a ring of flat, alpha-
            // blended panels (Sprites/Default, same shader used for the clouds) with a
            // dense-canopy silhouette texture, hiding the true horizon cheaply. Not
            // billboarded like the clouds - these are fixed, facing inward toward the
            // compound, since a ring of panels doesn't need to track the camera the way
            // individual cloud puffs do. Falls back to nothing (not an opaque
            // substitute) if the shader is missing, same reasoning as the clouds: this
            // is a purely decorative bonus, not core functionality.
            Shader treelineShader = Shader.Find("Sprites/Default");
            if (treelineShader != null)
            {
                // One wide texture, sliced into a unique horizontal strip per panel via
                // mainTextureScale/Offset (each panel's Renderer.material assignment
                // already instantiates its own material copy, so per-panel UV offsets
                // don't affect other panels) - this avoids the same tile obviously
                // repeating 28 times around the ring, which was the flat, uniformly
                // wavy look in the last screenshot.
                const int treelineSegments = 48;
                Texture2D treelineTexture = CreateTreelineTexture(2048, 160);
                Material treelineMaterial = new Material(treelineShader);
                treelineMaterial.mainTexture = treelineTexture;

                const float treelineHeight = 45f * 0.5f;
                float segmentWidth = (2f * Mathf.PI * treelineRadius / treelineSegments) * 1.15f;

                for (int s = 0; s < treelineSegments; s++)
                {
                    float angle = s * (Mathf.PI * 2f / treelineSegments);
                    float px = Mathf.Cos(angle) * treelineRadius;
                    float pz = Mathf.Sin(angle) * treelineRadius;

                    GameObject panel = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    panel.name = $"TreelinePanel_{s}";
                    Collider panelCollider = panel.GetComponent<Collider>();
                    if (panelCollider != null)
                    {
                        Object.Destroy(panelCollider);
                    }
                    panel.transform.SetParent(hangarRoot.transform, false);
                    panel.transform.localPosition = new Vector3(px, outsideGroundTopY + treelineHeight * 0.5f, pz);
                    // Facing inward: same reasoning as the cloud billboards (forward =
                    // the outward/away-from-viewer direction, so the visible face -
                    // which is on the opposite side - points back toward the compound).
                    panel.transform.localRotation = Quaternion.LookRotation(new Vector3(px, 0f, pz).normalized);
                    panel.transform.localScale = new Vector3(segmentWidth, treelineHeight, 1f);

                    Renderer panelRenderer = panel.GetComponent<Renderer>();
                    panelRenderer.material = treelineMaterial;
                    panelRenderer.material.mainTextureScale = new Vector2(1f / treelineSegments, 1f);
                    panelRenderer.material.mainTextureOffset = new Vector2((float)s / treelineSegments, 0f);
                }
                Debug.Log($"[DesignerBackgroundTest] Built distant treeline ring at radius {treelineRadius:F0} ({treelineSegments} segments, Sprites/Default).");
            }
            else
            {
                Debug.LogWarning("[DesignerBackgroundTest] Sprites/Default shader not found - skipping distant treeline ring.");
            }

            // --- Cafeteria annex ---
            // A small single-story building attached directly to the hangar's right
            // wall, at the back (away from the open hangar door). Connected via the
            // real doorway gap cut into RightWallBack/RightWallFront above - open on
            // the hangar-facing side (no fourth wall there), same as the hangar itself
            // is open on its +Z side, so it reads as one continuous connected space.
            const float cafeteriaWidth = 14f;
            const float cafeteriaDepth = 16f;
            const float cafeteriaHeight = 7f;
            const float cafeteriaWallThickness = 0.5f;
            float cafeteriaZEnd = cafeteriaZStart + cafeteriaDepth;
            float cafeteriaCenterZ = (cafeteriaZStart + cafeteriaZEnd) * 0.5f;
            float cafeteriaX0 = HalfWidth;
            float cafeteriaX1 = HalfWidth + cafeteriaWidth;
            float cafeteriaCenterX = (cafeteriaX0 + cafeteriaX1) * 0.5f;

            Texture2D tileFloorTexture = CreateTileFloorTexture();

            CreateBox(hangarRoot.transform, "CafeteriaFloor",
                new Vector3(cafeteriaCenterX, FloorTopY - WallThickness * 0.5f, cafeteriaCenterZ),
                new Vector3(cafeteriaWidth, WallThickness, cafeteriaDepth),
                // Tinted like the main hangar floor (concreteTint) instead of plain
                // white - the tile texture's own base colors are already light
                // (0.75-0.85), and without this same darkening multiplier the floor
                // rendered blown-out bright compared to the rest of the room.
                concreteTint, tileFloorTexture, new Vector2(cafeteriaWidth / 3f, cafeteriaDepth / 3f));

            // Outer wall (+X, away from the hangar) - mostly windows for natural light.
            CreateBox(hangarRoot.transform, "CafeteriaOuterWall",
                new Vector3(cafeteriaX1, FloorTopY + cafeteriaHeight * 0.5f, cafeteriaCenterZ),
                new Vector3(cafeteriaWallThickness, cafeteriaHeight, cafeteriaDepth),
                Color.white, windowWallTexture, new Vector2(cafeteriaDepth / 3f, cafeteriaHeight / 3f));

            // End walls (near the hangar's door side, and the far end) - also windowed.
            CreateBox(hangarRoot.transform, "CafeteriaWallNearDoor",
                new Vector3(cafeteriaCenterX, FloorTopY + cafeteriaHeight * 0.5f, cafeteriaZEnd),
                new Vector3(cafeteriaWidth, cafeteriaHeight, cafeteriaWallThickness),
                Color.white, windowWallTexture, new Vector2(cafeteriaWidth / 3f, cafeteriaHeight / 3f));
            CreateBox(hangarRoot.transform, "CafeteriaWallFarEnd",
                new Vector3(cafeteriaCenterX, FloorTopY + cafeteriaHeight * 0.5f, cafeteriaZStart),
                new Vector3(cafeteriaWidth, cafeteriaHeight, cafeteriaWallThickness),
                Color.white, windowWallTexture, new Vector2(cafeteriaWidth / 3f, cafeteriaHeight / 3f));

            CreateBox(hangarRoot.transform, "CafeteriaRoof",
                new Vector3(cafeteriaCenterX, FloorTopY + cafeteriaHeight + cafeteriaWallThickness * 0.5f, cafeteriaCenterZ),
                new Vector3(cafeteriaWidth, cafeteriaWallThickness, cafeteriaDepth),
                structureColor);

            // Ceiling lights.
            CreateLight(hangarRoot.transform, new Vector3(cafeteriaX0 + 4f, FloorTopY + cafeteriaHeight - 1f, cafeteriaZStart + 5f));
            CreateLight(hangarRoot.transform, new Vector3(cafeteriaX0 + 10f, FloorTopY + cafeteriaHeight - 1f, cafeteriaZStart + 5f));
            CreateLight(hangarRoot.transform, new Vector3(cafeteriaX0 + 7f, FloorTopY + cafeteriaHeight - 1f, cafeteriaZStart + 12f));

            // A handful of wandering Droods - the real imported Mixamo character/rig
            // (see WanderingDrood.cs) rather than a static prop, ambling around the
            // open ground-floor area (steering clear of the craft itself via the same
            // avoidance-radius technique as the launch pad's billboard trees) and,
            // sometimes, into the cafeteria through the real doorway gap in the right
            // wall - placed here (after the cafeteria is built) so the cafeteria's own
            // coordinate variables are in scope instead of being recomputed.
            const int droodCount = 10;
            float droodAvoidRadius = markRadius + 2f;
            float droodBoundsX = Mathf.Max(2f, HalfWidth - 8f);
            float droodBoundsZMin = -HalfDepth + 8f;
            float droodBoundsZMax = HalfDepth - 8f;
            Vector3 droodDoorwayPoint = new Vector3(HalfWidth - 0.5f, FloorTopY, cafeteriaDoorCenterZ);
            // Mirrors droodDoorwayPoint on the far side of the wall - without this,
            // a Drood routed straight from droodDoorwayPoint to a canteen target near
            // the far edge of the room could drift out of the (only 4m-wide) door
            // gap while still crossing the wall's own thickness, visibly clipping
            // through the wall right next to the doorway instead of passing through
            // it. Both points share the door's centre Z, so the short leg between
            // them - the one that actually crosses the wall - stays dead-centre in
            // the gap regardless of where the Drood is headed on either side.
            Vector3 droodDoorwayPointCanteenSide = new Vector3(HalfWidth + 2f, FloorTopY, cafeteriaDoorCenterZ);
            float droodCanteenMinX = cafeteriaX0 + 1.5f;
            float droodCanteenMaxX = cafeteriaX1 - 1.5f;
            float droodCanteenMinZ = cafeteriaZStart + 1.5f;
            float droodCanteenMaxZ = cafeteriaZEnd - 1.5f;
            System.Random droodRandom = new System.Random(13579);
            // Same StructureEditController lookup the tree-exclusion code and the
            // later Sync calls use - fetched here too since Droods spawn before that
            // later lookup runs, and wander-target picking needs it to steer clear of
            // whatever's been placed via the F2 editor.
            StructureEditController droodStructureSource = cam.gameObject.GetComponent<StructureEditController>();

            // Stairs aren't F2-placed structures (they're part of the hangar shell
            // itself), so they never show up in droodStructureSource's footprint
            // list - Droods were walking straight through them. Same left/right X
            // the stairs themselves were built from, further up in this method -
            // one rectangular keep-out per side. Only the bottom half of the run
            // (from the floor-level base up to the halfway point) rather than the
            // whole flight up to stairTopZ: the upper half is elevated well above
            // ground level by then, so it doesn't actually intersect where a Drood
            // walks, and keeping the zone shorter leaves more room to route around
            // it near the base - the tight corner it used to get squeezed into
            // (see the propellant tanks removed above, right next to this zone).
            List<Bounds> droodExtraAvoidZones = new List<Bounds>();
            float stairZoneHalfwayZ = (stairTopZ + stairBaseZ) * 0.5f;
            float stairZoneMinZ = Mathf.Min(stairZoneHalfwayZ, stairBaseZ);
            float stairZoneMaxZ = Mathf.Max(stairZoneHalfwayZ, stairBaseZ);
            foreach (float stairX in new[] { leftStairWalkwayX, rightStairWalkwayX })
            {
                Bounds stairZone = new Bounds();
                stairZone.SetMinMax(
                    new Vector3(stairX - stairTreadWidth * 0.5f, FloorTopY - 5f, stairZoneMinZ),
                    new Vector3(stairX + stairTreadWidth * 0.5f, FloorTopY + 50f, stairZoneMaxZ));
                droodExtraAvoidZones.Add(stairZone);
            }

            // Old Droods (and this registry's references to them) are about to be
            // destroyed along with the previous hangarRoot - clear it first so the
            // conversation matchmaker doesn't hold onto stale entries.
            DroodSocialCoordinator.Clear();
            for (int d = 0; d < droodCount; d++)
            {
                float startAngle = (float)droodRandom.NextDouble() * Mathf.PI * 2f;
                float startRadius = droodAvoidRadius + 3f + (float)droodRandom.NextDouble() * 6f;
                Vector3 startPos = new Vector3(
                    Mathf.Clamp(Mathf.Cos(startAngle) * startRadius, -droodBoundsX, droodBoundsX),
                    FloorTopY,
                    Mathf.Clamp(Mathf.Sin(startAngle) * startRadius, droodBoundsZMin, droodBoundsZMax));

                WanderingDrood drood = DroodFactory.Create(hangarRoot.transform, startPos, FloorTopY, droodRandom, walkCamera);
                if (drood == null)
                {
                    continue;
                }
                drood.WanderMinX = -droodBoundsX;
                drood.WanderMaxX = droodBoundsX;
                drood.WanderMinZ = droodBoundsZMin;
                // +20 only on this (max Z) side - the hangar's open +Z "door" side
                // (see BuildHangar's wall layout, there's no wall there) - so wander
                // targets can legitimately land up to 20m out front. Every other
                // side (back wall at WanderMinZ, left/right at WanderMinX/MaxX)
                // stays at the wall-clearance bound, no slack.
                drood.WanderMaxZ = droodBoundsZMax + 20f;
                drood.AvoidRadius = droodAvoidRadius;
                drood.StructureSource = droodStructureSource;
                drood.ExtraAvoidZones = droodExtraAvoidZones;
                drood.DoorwayPoint = droodDoorwayPoint;
                drood.DoorwayPointCanteenSide = droodDoorwayPointCanteenSide;
                drood.CanteenMinX = droodCanteenMinX;
                drood.CanteenMaxX = droodCanteenMaxX;
                drood.CanteenMinZ = droodCanteenMinZ;
                drood.CanteenMaxZ = droodCanteenMaxZ;
            }

            // Clouds: try soft alpha-blended billboards first. Sprites/Default is used
            // by virtually every Unity game's UI, so unlike the uncommon Skybox/
            // Panoramic shader that turned out to be stripped from this game's build
            // earlier this session, it's very unlikely to be missing - but if it is,
            // fall back to the old opaque sphere-cluster look rather than having
            // clouds silently disappear.
            Shader spriteShader = Shader.Find("Sprites/Default");
            System.Random cloudRandom = new System.Random(12345);
            if (spriteShader != null)
            {
                Texture2D cloudPuffTexture = CreateCloudPuffTexture();
                Material cloudMaterial = new Material(spriteShader);
                cloudMaterial.mainTexture = cloudPuffTexture;

                for (int c = 0; c < 12; c++)
                {
                    float cx = ((float)cloudRandom.NextDouble() * 2f - 1f) * 350f;
                    float cz = ((float)cloudRandom.NextDouble() * 2f - 1f) * 350f;
                    float cy = FloorTopY + 220f + (float)cloudRandom.NextDouble() * 80f;

                    GameObject cloudCluster = new GameObject($"CloudSoft_{c}");
                    cloudCluster.transform.SetParent(hangarRoot.transform, false);
                    cloudCluster.transform.localPosition = new Vector3(cx, cy, cz);

                    int puffCount = 4 + cloudRandom.Next(4);
                    for (int p = 0; p < puffCount; p++)
                    {
                        float ox = ((float)cloudRandom.NextDouble() * 2f - 1f) * 100f;
                        float oz = ((float)cloudRandom.NextDouble() * 2f - 1f) * 100f;
                        float oy = ((float)cloudRandom.NextDouble() * 2f - 1f) * 30f;
                        float puffSize = (14f + (float)cloudRandom.NextDouble() * 10f) * 10f;

                        GameObject puff = GameObject.CreatePrimitive(PrimitiveType.Quad);
                        puff.name = $"Puff_{p}";
                        Collider puffCollider = puff.GetComponent<Collider>();
                        if (puffCollider != null)
                        {
                            Object.Destroy(puffCollider);
                        }
                        puff.transform.SetParent(cloudCluster.transform, false);
                        puff.transform.localPosition = new Vector3(ox, oy, oz);
                        puff.transform.localScale = new Vector3(puffSize, puffSize, 1f);

                        Renderer puffRenderer = puff.GetComponent<Renderer>();
                        puffRenderer.material = cloudMaterial;
                        float brightness = 0.9f + (float)cloudRandom.NextDouble() * 0.1f;
                        puffRenderer.material.color = new Color(brightness, brightness, brightness, 1f);

                        BillboardFacer billboard = puff.AddComponent<BillboardFacer>();
                        billboard.TargetCamera = cam;
                    }
                }
                Debug.Log("[DesignerBackgroundTest] Built soft billboard clouds (Sprites/Default).");
            }
            else
            {
                Debug.LogWarning("[DesignerBackgroundTest] Sprites/Default shader not found - falling back to opaque sphere-cluster clouds.");
                Color cloudColor = new Color(0.97f, 0.97f, 0.98f);
                for (int c = 0; c < 12; c++)
                {
                    float cx = ((float)cloudRandom.NextDouble() * 2f - 1f) * 350f;
                    float cz = ((float)cloudRandom.NextDouble() * 2f - 1f) * 350f;
                    float cy = FloorTopY + 220f + (float)cloudRandom.NextDouble() * 80f;

                    GameObject cloudCluster = new GameObject($"Cloud_{c}");
                    cloudCluster.transform.SetParent(hangarRoot.transform, false);
                    cloudCluster.transform.localPosition = new Vector3(cx, cy, cz);

                    int puffCount = 3 + cloudRandom.Next(3);
                    for (int p = 0; p < puffCount; p++)
                    {
                        float ox = ((float)cloudRandom.NextDouble() * 2f - 1f) * 80f;
                        float oz = ((float)cloudRandom.NextDouble() * 2f - 1f) * 80f;
                        float oy = ((float)cloudRandom.NextDouble() * 2f - 1f) * 20f;
                        float widthDiameter = (8f + (float)cloudRandom.NextDouble() * 6f) * 10f;
                        float flatDiameter = widthDiameter * 0.5f;
                        CreateSphere(cloudCluster.transform, $"Puff_{p}",
                            new Vector3(ox, oy, oz),
                            new Vector3(widthDiameter, flatDiameter, widthDiameter),
                            cloudColor);
                    }
                }
            }

            // Keep the structure editor's "Add" spawn point next to whichever hangar
            // is currently built - the structures it has already placed live under
            // their own persistent root (see Postfix) and are untouched by this, only
            // newly-added ones land here.
            StructureEditController structureEditor = cam.gameObject.GetComponent<StructureEditController>();
            if (structureEditor != null)
            {
                structureEditor.SpawnPosition = new Vector3(HalfWidth + 60f, FloorTopY, 0f);
                // Already-placed structures live under a persistent root untouched by
                // the hangar rebuild, but FloorTopY itself is recalculated from the
                // current craft's bounds every time - without this they'd stay at
                // whatever world Y they were placed at while the actual floor moves
                // out from under them (floating or sinking after a refresh/reload).
                structureEditor.SyncFloorY(FloorTopY);
                // Keeps any prop with "Anchor to nearest wall" toggled on flush
                // against that wall as the hangar's footprint grows/shrinks with the
                // craft - see StructureEditController.SyncWallAnchors.
                structureEditor.SyncWallAnchors(HalfWidth, HalfDepth);

                // Cars loop through whatever's flagged "Waypoint for car path" in the
                // F2 editor (see StructureEditController.GetWaypointPositions) - only
                // spawns once there's at least one to drive toward. Parented under
                // hangarRoot (unlike the placed structures themselves) so cars get
                // cleanly destroyed and respawned on every refresh, same as the
                // Droods, rather than persisting stale references across rebuilds.
                List<Vector3> carWaypoints = structureEditor.GetWaypointPositions();
                if (carWaypoints.Count > 0)
                {
                    // Fixed mix (was 2x M939Truck + 1x RangeRover; car 1 swapped to
                    // CargoTruck) rather than a random roll.
                    CarFactory.VehicleKind[] carTypes =
                    {
                        CarFactory.VehicleKind.CargoTruck,
                        CarFactory.VehicleKind.M939Truck,
                        CarFactory.VehicleKind.RangeRover,
                    };
                    for (int i = 0; i < carTypes.Length; i++)
                    {
                        int startIndex = i % carWaypoints.Count;
                        CarFactory.Create(hangarRoot.transform, carWaypoints[startIndex], FloorTopY, structureEditor, startIndex, carTypes[i]);
                    }
                }
            }

            Debug.Log($"[DesignerBackgroundTest] Built hangar shell: {hangarRoot.transform.childCount} objects under '{hangarRoot.name}'.");
        }

        // Finds the designer's real build-platform object (Assets.Scripts.Design.
        // DesignerScript.DesignerPlatform - confirmed via reflection, not a name/size
        // guess like an earlier failed attempt) and disables every renderer under it so
        // the hex pad is no longer visible.
        private static void HideDesignerPlatform(Assets.Scripts.Design.DesignerScript designerScript)
        {
            Assets.Scripts.Design.DesignerPlatformScript platform = designerScript?.DesignerPlatform;
            if (platform == null)
            {
                Debug.LogWarning("[DesignerBackgroundTest] DesignerScript.DesignerPlatform was null - cannot hide it.");
                return;
            }

            Renderer[] renderers = platform.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in renderers)
            {
                r.enabled = false;
            }
            Debug.Log($"[DesignerBackgroundTest] Hid {renderers.Length} DesignerPlatform renderer(s).");
        }

        private static void CreateBox(Transform parent, string name, Vector3 position, Vector3 size, Color color, Texture2D texture = null, Vector2 textureScale = default)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent, false);
            box.transform.localPosition = position;
            box.transform.localScale = size;

            Material material = box.GetComponent<Renderer>().material;
            material.color = color;
            if (texture != null)
            {
                material.mainTexture = texture;
                material.mainTextureScale = textureScale;
            }

            Collider collider = box.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }
        }

        // Unity's cylinder primitive is 2 units tall along its local Y axis by default,
        // so scale.y = length / 2 gives the requested length; rotate to lay it on its
        // side (e.g. Quaternion.Euler(0,0,90) to run along X).
        private static void CreateCylinder(Transform parent, string name, Vector3 position, Quaternion rotation, float diameter, float length, Color color)
        {
            GameObject cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cyl.name = name;
            cyl.transform.SetParent(parent, false);
            cyl.transform.localPosition = position;
            cyl.transform.localRotation = rotation;
            cyl.transform.localScale = new Vector3(diameter, length * 0.5f, diameter);
            cyl.GetComponent<Renderer>().material.color = color;

            Collider collider = cyl.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }
        }

        // Connects two points with a thin box, orienting it via FromToRotation rather
        // than a hand-picked Euler angle - robust regardless of the direction between
        // the points, unlike guessing a rotation sign (which is what made the earlier
        // pitched-roof idea too risky to attempt without visual testing).
        private static void CreateBoxBetween(Transform parent, string name, Vector3 a, Vector3 b, float thickness, Color color)
        {
            Vector3 direction = b - a;
            float length = direction.magnitude;
            if (length < 0.0001f)
            {
                return;
            }

            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent, false);
            box.transform.localPosition = (a + b) * 0.5f;
            box.transform.localRotation = Quaternion.FromToRotation(Vector3.up, direction / length);
            box.transform.localScale = new Vector3(thickness, length, thickness);
            box.GetComponent<Renderer>().material.color = color;

            Collider collider = box.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }
        }

        private static void CreateLight(Transform parent, Vector3 position)
        {
            GameObject lightObj = new GameObject("WorkLight");
            lightObj.transform.SetParent(parent, false);
            lightObj.transform.localPosition = position;
            Light light = lightObj.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.92f, 0.78f);
            light.intensity = 1.5f;
            light.range = 30f;
        }

        // Dining table: a tabletop plus four thin legs, all at world-axis orientation
        // (no rotation needed since tables don't need to face anything).
        private static void CreateTable(Transform parent, string name, Vector3 center, float floorY)
        {
            Color tableColor = new Color(0.42f, 0.28f, 0.16f);
            CreateBox(parent, name + "_Top", new Vector3(center.x, floorY + 0.75f, center.z), new Vector3(1.4f, 0.08f, 1.4f), tableColor);
            float legOffset = 0.6f;
            Vector3[] legOffsets =
            {
                new Vector3(legOffset, 0f, legOffset), new Vector3(-legOffset, 0f, legOffset),
                new Vector3(legOffset, 0f, -legOffset), new Vector3(-legOffset, 0f, -legOffset),
            };
            for (int i = 0; i < legOffsets.Length; i++)
            {
                CreateCylinder(parent, $"{name}_Leg_{i}",
                    new Vector3(center.x + legOffsets[i].x, floorY + 0.375f, center.z + legOffsets[i].z),
                    Quaternion.identity, 0.08f, 0.75f, tableColor);
            }
        }

        // Chair: seat, backrest and four legs, built under its own rotated root so the
        // whole assembly faces whichever way yawDegrees points without any per-part
        // rotation math.
        private static void CreateChair(Transform parent, string name, Vector3 center, float yawDegrees, float floorY, Color color)
        {
            GameObject chairRoot = new GameObject(name);
            chairRoot.transform.SetParent(parent, false);
            chairRoot.transform.localPosition = new Vector3(center.x, floorY, center.z);
            chairRoot.transform.localRotation = Quaternion.Euler(0f, yawDegrees, 0f);

            CreateBox(chairRoot.transform, "Seat", new Vector3(0f, 0.45f, 0f), new Vector3(0.5f, 0.08f, 0.5f), color);
            CreateBox(chairRoot.transform, "Back", new Vector3(0f, 0.75f, -0.22f), new Vector3(0.5f, 0.6f, 0.06f), color);
            float legOff = 0.2f;
            Vector3[] legOffsets =
            {
                new Vector3(legOff, 0f, legOff), new Vector3(-legOff, 0f, legOff),
                new Vector3(legOff, 0f, -legOff), new Vector3(-legOff, 0f, -legOff),
            };
            for (int i = 0; i < legOffsets.Length; i++)
            {
                CreateBox(chairRoot.transform, $"Leg_{i}", new Vector3(legOffsets[i].x, 0.225f, legOffsets[i].z), new Vector3(0.05f, 0.45f, 0.05f), color);
            }
        }

        // Couch: seat cushion, backrest and two armrests, same rotated-root approach as
        // CreateChair so it can face any direction.
        private static void CreateCouch(Transform parent, string name, Vector3 center, float yawDegrees, float floorY, Color color)
        {
            GameObject couchRoot = new GameObject(name);
            couchRoot.transform.SetParent(parent, false);
            couchRoot.transform.localPosition = new Vector3(center.x, floorY, center.z);
            couchRoot.transform.localRotation = Quaternion.Euler(0f, yawDegrees, 0f);

            CreateBox(couchRoot.transform, "Seat", new Vector3(0f, 0.35f, 0f), new Vector3(2.2f, 0.5f, 0.9f), color);
            CreateBox(couchRoot.transform, "Back", new Vector3(0f, 0.75f, -0.4f), new Vector3(2.2f, 0.7f, 0.2f), color);
            CreateBox(couchRoot.transform, "ArmLeft", new Vector3(-1.05f, 0.55f, 0f), new Vector3(0.2f, 0.5f, 0.9f), color);
            CreateBox(couchRoot.transform, "ArmRight", new Vector3(1.05f, 0.55f, 0f), new Vector3(0.2f, 0.5f, 0.9f), color);
        }

        private static void CreateSphere(Transform parent, string name, Vector3 position, float diameter, Color color)
        {
            CreateSphere(parent, name, position, new Vector3(diameter, diameter, diameter), color);
        }

        private static void CreateSphere(Transform parent, string name, Vector3 position, Vector3 scale, Color color)
        {
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = name;
            sphere.transform.SetParent(parent, false);
            sphere.transform.localPosition = position;
            sphere.transform.localScale = scale;
            sphere.GetComponent<Renderer>().material.color = color;

            Collider collider = sphere.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }
        }

        // Dense tree-canopy silhouette for the distant treeline panels: opaque green
        // below a jagged canopy-top line, fully transparent above it so the sky shows
        // through - the irregular top edge is what sells it as a treeline rather than
        // a flat green rectangle. The top line combines three Perlin octaves at very
        // different frequencies (broad rolling shape, medium clumps, fine jitter)
        // instead of one smooth wave, which is what made the first version look like a
        // uniform sawtooth repeating around the ring. Sampled over the normalized 0-1
        // width regardless of pixel resolution, so this stays consistent whichever
        // texture size is requested - the caller can go wide (one big texture sliced
        // per ring panel) without the pattern changing character.
        // A single tree card for the mid-distance billboard band: a narrow brown trunk
        // strip topped by an irregular (Perlin-edged) green canopy blob, transparent
        // everywhere else. Only ever used beyond real-tree walking distance, since a
        // billboard is paper-thin from the side - see YAxisBillboardFacer for why this
        // stays upright instead of using the full 3-axis BillboardFacer.
        private static Texture2D CreateBillboardTreeTexture()
        {
            const int width = 64;
            const int height = 96;

            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            Vector2 canopyCenter = new Vector2(width * 0.5f, height * 0.62f);
            float canopyRadiusX = width * 0.42f;
            float canopyRadiusY = height * 0.34f;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float dx = (x - canopyCenter.x) / canopyRadiusX;
                    float dy = (y - canopyCenter.y) / canopyRadiusY;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float edgeNoise = Mathf.PerlinNoise(x * 0.2f, y * 0.2f);
                    float edgeDist = dist - (0.8f + edgeNoise * 0.25f);
                    bool inCanopy = edgeDist < 0f;

                    bool inTrunk = !inCanopy
                        && Mathf.Abs(x - width * 0.5f) < width * 0.06f
                        && y < canopyCenter.y + canopyRadiusY * 0.3f;

                    if (inCanopy)
                    {
                        float shade = Mathf.PerlinNoise(x * 0.3f, y * 0.3f);
                        Color c = new Color(Mathf.Lerp(0.16f, 0.28f, shade), Mathf.Lerp(0.32f, 0.48f, shade), Mathf.Lerp(0.12f, 0.22f, shade), 1f);
                        c.a = Mathf.Clamp01(-edgeDist * 6f);
                        tex.SetPixel(x, y, c);
                    }
                    else if (inTrunk)
                    {
                        tex.SetPixel(x, y, new Color(0.32f, 0.22f, 0.13f, 1f));
                    }
                    else
                    {
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                    }
                }
            }
            tex.Apply();
            return tex;
        }

        private static Texture2D CreateTreelineTexture(int width, int height)
        {
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;

            for (int x = 0; x < width; x++)
            {
                float u = x / (float)width;
                float broad = Mathf.PerlinNoise(u * 40f, 0f);
                float medium = Mathf.PerlinNoise(u * 140f, 10f);
                float fine = Mathf.PerlinNoise(u * 400f, 20f);
                float combined = broad * 0.55f + medium * 0.3f + fine * 0.15f;
                float canopyTopFrac = Mathf.Lerp(0.42f, 0.88f, combined);
                int canopyTopPixel = Mathf.RoundToInt(canopyTopFrac * height);

                for (int y = 0; y < height; y++)
                {
                    if (y > canopyTopPixel)
                    {
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                        continue;
                    }

                    float shadeNoise = Mathf.PerlinNoise(u * 200f, y * 0.2f);
                    float darkness = Mathf.Lerp(0.15f, 0.32f, shadeNoise);
                    Color c = new Color(darkness * 0.7f, darkness + 0.05f, darkness * 0.5f, 1f);

                    // Feather the last couple of pixels below the canopy line so the
                    // silhouette edge isn't perfectly razor-sharp.
                    int edgeDist = canopyTopPixel - y;
                    if (edgeDist < 2)
                    {
                        c.a = Mathf.Clamp01(edgeDist / 2f);
                    }
                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply();
            return tex;
        }

        // Soft round "puff" sprite for cloud billboards: white with a smoothstep radial
        // alpha falloff (opaque center, fully transparent at the edge), with a little
        // Perlin irregularity so it doesn't read as a perfect circle. Needs an alpha
        // channel, unlike every other texture here, since it's meant for Sprites/
        // Default's alpha-blended rendering rather than an opaque material.
        private static Texture2D CreateCloudPuffTexture()
        {
            const int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);
            float maxDist = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), center) / maxDist;
                    float alpha = Mathf.Clamp01(1f - dist);
                    alpha = alpha * alpha * (3f - 2f * alpha); // smoothstep for a softer falloff
                    float n = Mathf.PerlinNoise(x * 0.15f, y * 0.15f);
                    alpha *= Mathf.Lerp(0.7f, 1f, n);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply();
            return tex;
        }

        // Grass: layered Perlin noise between two greens, so it reads as mottled turf
        // rather than a flat color, tiled every 4 world units via textureScale.
        private static Texture2D CreateGrassTexture()
        {
            const int size = 128;
            Color darkGrass = new Color(0.20f, 0.34f, 0.14f);
            Color lightGrass = new Color(0.34f, 0.5f, 0.22f);

            Texture2D tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float coarse = Mathf.PerlinNoise(x * 0.04f, y * 0.04f);
                    float fine = Mathf.PerlinNoise(x * 0.25f, y * 0.25f);
                    float blend = Mathf.Clamp01(coarse * 0.7f + fine * 0.3f);
                    Color c = Color.Lerp(darkGrass, lightGrass, blend);
                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply();
            return tex;
        }

        // Concrete floor panels: base grey with subtle noise mottling and darker seam
        // lines every 1/5th of the texture (tiled every 5 world units via textureScale).
        // Light checkerboard tile, for the cafeteria floor - distinct from the
        // industrial hangar concrete, tiled every 3 world units via textureScale.
        private static Texture2D CreateTileFloorTexture()
        {
            const int size = 64;
            const int half = size / 2;
            Color tileA = new Color(0.85f, 0.83f, 0.78f);
            Color tileB = new Color(0.75f, 0.73f, 0.68f);

            Texture2D tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool checkerA = ((x / half) + (y / half)) % 2 == 0;
                    tex.SetPixel(x, y, checkerA ? tileA : tileB);
                }
            }
            tex.Apply();
            return tex;
        }

        private static Texture2D CreateFloorTexture()
        {
            const int size = 256;
            const int panels = 5;
            int panelSize = size / panels;
            Color baseColor = new Color(0.5f, 0.5f, 0.5f);
            Color seamColor = new Color(0.3f, 0.3f, 0.3f);

            Texture2D tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float n = (Mathf.PerlinNoise(x * 0.08f, y * 0.08f) - 0.5f) * 0.12f;
                    Color c = new Color(baseColor.r + n, baseColor.g + n, baseColor.b + n);

                    bool seam = (x % panelSize) < 2 || (y % panelSize) < 2;
                    tex.SetPixel(x, y, seam ? seamColor : c);
                }
            }
            tex.Apply();
            return tex;
        }

        // Corrugated metal siding: alternating vertical ridges, tiled horizontally and
        // vertically via textureScale.
        private static Texture2D CreateWallTexture()
        {
            const int width = 64;
            const int height = 64;
            const int ribWidth = 8;
            Color baseColor = new Color(0.6f, 0.61f, 0.63f);
            Color ribShadeColor = new Color(0.48f, 0.49f, 0.51f);
            Color grimeColor = new Color(0.4f, 0.4f, 0.4f);

            Texture2D tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int ribPhase = x % ribWidth;
                    float shade = Mathf.Sin(ribPhase / (float)ribWidth * Mathf.PI); // 0 at edges, 1 at rib center
                    Color c = Color.Lerp(ribShadeColor, baseColor, shade);

                    // A subtle dark band near the bottom edge of each repeat (texture tiles
                    // up the wall, so this reads as a repeating wear/grime line rather than
                    // one continuous streak from floor to roof).
                    float grimeAmount = Mathf.Clamp01(0.3f - y / (float)height) / 0.3f * (0.3f + 0.7f * Mathf.PerlinNoise(x * 0.3f, 0f));
                    c = Color.Lerp(c, grimeColor, grimeAmount * 0.4f);

                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply();
            return tex;
        }

        // Classic diagonal yellow/black caution stripes for column safety-marking bases.
        private static Texture2D CreateHazardStripeTexture()
        {
            const int size = 64;
            Color yellow = new Color(0.95f, 0.75f, 0.05f);
            Color black = new Color(0.08f, 0.08f, 0.08f);

            Texture2D tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Point; // crisp stripe edges

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int diag = (x + y) % 16;
                    tex.SetPixel(x, y, diag < 8 ? yellow : black);
                }
            }
            tex.Apply();
            return tex;
        }

        // Multi-pane glass wall: pale blue-white "glass" divided by dark mullions into
        // a grid, tiled via textureScale like the other wall textures.
        private static Texture2D CreateWindowWallTexture()
        {
            const int size = 64;
            const int paneCount = 4;
            int paneSize = size / paneCount;
            Color glassColor = new Color(0.82f, 0.9f, 0.95f);
            Color mullionColor = new Color(0.25f, 0.26f, 0.28f);

            Texture2D tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool onMullion = (x % paneSize) < 2 || (y % paneSize) < 2;
                    if (onMullion)
                    {
                        tex.SetPixel(x, y, mullionColor);
                        continue;
                    }

                    // A soft vertical gradient within each pane, like light glare.
                    float paneLocalY = (y % paneSize) / (float)paneSize;
                    Color c = Color.Lerp(glassColor, Color.white, (1f - paneLocalY) * 0.25f);
                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply();
            return tex;
        }
    }
}
