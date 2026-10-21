using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine.Events;

namespace Shapez2Multiplayer
{
    // Fresh profiles have a New Game entry instead of Play. UILib's registered
    // add-after-Play hook therefore cannot be the only way into multiplayer.
    [HarmonyPatch(typeof(HUDMenuMainState), "RebuildMenuButtons")]
    internal static class CoopMainMenuEntry
    {
        [HarmonyPostfix]
        private static void Postfix(HUDMenuMainState __instance, List<HUDMenuButton> ___MenuButtonInstances,
            IMainMenuStateControl ___Menu)
        {
            if (___MenuButtonInstances.Any(b => b.name == "CoopMultiplayerEntry")) return;
            int position = ___MenuButtonInstances.Count == 0 ? 0 : ___MenuButtonInstances[0].transform.GetSiblingIndex() + 1;
            AccessTools.Method(typeof(HUDMenuMainState), "AddMenuButton").Invoke(__instance, new object[] {
                Shapez2Multiplayer.MultiplayerButtonTranslation,
                new UnityAction(() => ___Menu.SwitchToState<HUDMenuMultiplayerState>())
            });
            var entry = ___MenuButtonInstances[___MenuButtonInstances.Count - 1];
            entry.name = "CoopMultiplayerEntry";
            entry.transform.SetSiblingIndex(position);
            Shapez2Multiplayer.logger.Info?.Log("Co-op main menu entry ready");
        }
    }
}
