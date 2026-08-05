namespace Hades.Core.Unity;

/// <summary>
/// Interprets a parsed <see cref="UnityObject"/> as a PrefabInstance, when it is one.
/// Prefab instancing is overwhelmingly a SCENE phenomenon — 386 instances across 25 of 49 scenes
/// against 33 across 28 of 114 prefabs — so this is what makes scene contents resolvable.
/// </summary>
public static class PrefabInstanceReader
{
    const int PrefabInstanceClassId = 1001;

    public static PrefabInstanceInfo? TryRead(UnityObject obj)
    {
        if (obj.ClassId != PrefabInstanceClassId) return null;

        // Class 1001 covers TWO formats. The modern one is "PrefabInstance:" with m_SourcePrefab.
        // The pre-2018.3 one is "Prefab:" with m_ParentPrefab / m_IsPrefabParent, and it marks a
        // prefab ASSET rather than an instance of one. Unity still loads those, and real projects
        // still contain them — 15 in the measured corpus, all from third-party packages shipped
        // years ago and never re-saved. They instantiate nothing, so there is no instance_of edge
        // to draw; returning null is correct, but it is a recognised case, not an accident.
        if (obj.DeclaredTypeName is "Prefab") return null;

        var sourcePrefab = obj.References.FirstOrDefault(r => r.PropertyPath == "m_SourcePrefab");
        if (sourcePrefab is null) return null;

        return new PrefabInstanceInfo
        {
            FileId = obj.FileId,
            SourcePrefab = sourcePrefab,
            // Absent means {fileID: 0}, which the reader drops as Unity's null — and a null
            // parent is exactly what marks a variant root.
            TransformParent = obj.References
                .FirstOrDefault(r => r.PropertyPath == "m_Modification.m_TransformParent"),
            ReferenceOverrides = obj.Modifications.Where(m => m.IsReferenceOverride).ToList(),
        };
    }
}
