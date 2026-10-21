using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using HarmonyLib;

namespace Shapez2Multiplayer
{
    // A simulation test does not operate the optional DLC catalogue. Its async
    // builders can outlive a menu disposed by the automated join, so do not start
    // those cosmetic tasks in this QA-only build. Gameplay exceptions still fail QA.
    [HarmonyPatch]
    internal static class CoopQaStoreCatalog
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(HUDAdditionalContentTabPacks), "BuildView");
            yield return AccessTools.Method(typeof(HUDAdditionalContentSkinsGridSection), "BuildView");
        }
        [HarmonyPrefix] private static bool Prefix(ref UniTaskVoid __result)
        { __result = default; return false; }
    }
}
