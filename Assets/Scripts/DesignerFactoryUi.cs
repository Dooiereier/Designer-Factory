using System.Linq;
using System.Xml.Linq;
using ModApi.Ui;
using UnityEngine;

namespace DesignerBackgroundTest
{
    // Adds a "Refresh Hangar" icon button to the Designer's own right-side toolbar
    // (the vertical strip with Launch/NavCube/Performance/Crew/EditProgram - it lands
    // directly below EditProgram, the last one in that strip). This is the same
    // sanctioned modding pattern other Juno mods (e.g. Ember) use to add real UI:
    // Game.Instance.UserInterface.AddBuildUserInterfaceXmlAction lets a mod rewrite
    // the game's own XML UI definition before it's built, rather than needing to
    // Harmony-patch whatever script constructs it. Confirmed against this project's
    // own Assets/ModTools/UI/Xml/Design/DesignerUi.xml (shipped for reference).
    //
    // Click handling deliberately skips the XML "onClick=..." attribute (which
    // resolves a method-name string against a specific controller object, and would
    // need one to already exist on DesignerUiScript or a custom ChildXmlLayout
    // controller) in favor of IXmlElement.AddOnClickEvent, a plain C# delegate hookup
    // done once the real UI exists - simpler and doesn't risk breaking the rest of
    // the Designer's UI if a name fails to resolve.
    public static class DesignerFactoryUi
    {
        private const string ButtonId = "designer-factory-refresh-hangar-button";

        public static void OnBuildDesignUi(BuildUserInterfaceXmlRequest request)
        {
            XNamespace ns = XmlLayoutConstants.XmlNamespace;

            // The "EditProgram" (Vizzy) button is the last entry in the right-side
            // toolbar's VerticalLayout - find it by its onClick, then add our own
            // button as the next sibling so it lands directly below it.
            XElement editProgramButton = request.XmlDocument
                .Descendants(ns + "Panel")
                .FirstOrDefault(x => (string)x.Attribute("onClick") == "OnEditProgramButtonClicked();");

            if (editProgramButton == null)
            {
                Debug.LogWarning("[DesignerBackgroundTest] Could not find the EditProgram toolbar button to attach the Refresh Hangar button next to - Juno's DesignerUi.xml layout may have changed.");
                return;
            }

            XElement refreshButton = new XElement(ns + "Panel",
                new XAttribute("id", ButtonId),
                new XAttribute("class", "toggle-button tool-button audio-btn-click"),
                new XAttribute("tooltip", "Refresh Hangar"),
                new XElement(ns + "Image",
                    new XAttribute("class", "toggle-button-icon toggle-button-icon-white"),
                    new XAttribute("sprite", "Ui/Sprites/Design/IconResetStaging")));

            editProgramButton.AddAfterSelf(refreshButton);

            // Wire the actual click once the real UI elements exist - AddOnClickEvent
            // takes a plain delegate, so there's no method-name-string resolution to
            // get wrong.
            request.AddOnLayoutRebuiltAction(controller =>
            {
                IXmlElement button = controller.XmlLayout.GetElementById(ButtonId);
                if (button == null)
                {
                    Debug.LogWarning("[DesignerBackgroundTest] Refresh Hangar button XML was added but the element couldn't be found after layout rebuild.");
                    return;
                }
                button.AddOnClickEvent(DesignerBackgroundTestPatch.RefreshHangar, true);
            });
        }
    }
}
