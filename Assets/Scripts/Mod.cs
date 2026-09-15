namespace Assets.Scripts
{
    using System.Linq;
    using System.Xml.Linq;
    using HarmonyLib;
    using ModApi.Ui;
    using UnityEngine;

    public class Mod : ModApi.Mods.GameMod
    {
        private Mod() : base()
        {
        }

        public static Mod Instance { get; } = GetModInstance<Mod>();

        protected override void OnModInitialized()
        {
            Harmony harmony = new Harmony("DesignerFactory");
            harmony.PatchAll();

            // Adds a "Refresh Hangar" icon button to the Designer's own right-side
            // toolbar (below the Vizzy/"EditProgram" icon), following the same
            // sanctioned XML-injection pattern other Juno mods (e.g. Ember) use to
            // add real UI - not Harmony-patching the game's UI construction, just
            // hooking the documented Game.Instance.UserInterface.
            // AddBuildUserInterfaceXmlAction extension point. See
            // DesignerBackgroundTest.DesignerFactoryUi for the actual XML/click wiring.
            Game.Instance.UserInterface.AddBuildUserInterfaceXmlAction(UserInterfaceIds.Design.DesignerUi, DesignerBackgroundTest.DesignerFactoryUi.OnBuildDesignUi);

            DesignerBackgroundTest.ModUpdater.CheckForUpdate();
        }
    }
}
