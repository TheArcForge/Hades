using UnityEditor;

namespace ArcForge.Hades.Editor.Graph.Updates
{
    public class GraphAssetPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (GraphUpdateHandler.Instance == null) return;
            if (GraphUpdateHandler.Instance.IsBusy) return;

            if (deletedAssets.Length > 0)
                GraphUpdateHandler.Instance.OnAssetsDeleted(deletedAssets);

            if (movedAssets.Length > 0)
                GraphUpdateHandler.Instance.OnAssetsMoved(movedFromAssetPaths, movedAssets);

            if (importedAssets.Length > 0)
            {
                var guids = new string[importedAssets.Length];
                for (int i = 0; i < importedAssets.Length; i++)
                    guids[i] = AssetDatabase.AssetPathToGUID(importedAssets[i]);
                GraphUpdateHandler.Instance.OnAssetsChanged(guids);
            }
        }
    }
}
