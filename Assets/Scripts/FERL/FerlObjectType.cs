using System;

// The scene-graph object types preference_rl understands, in the exact order of
// preference_rl.preference.scenecorr.scene_graph.OBJECT_TYPES (the one-hot type
// embedding is positional, so the order is part of the contract).
public enum FerlObjectType
{
    Laptop = 0,
    Cup = 1,
    Table = 2,
    Human = 3,
    Marker = 4,
    Other = 5,
}

public static class FerlObjectTypes
{
    public static readonly string[] Names = { "laptop", "cup", "table", "human", "marker", "other" };

    // preference_rl's ATTRIBUTE_NAMES, in order: the attribute vector is positional too.
    public static readonly string[] AttributeNames = { "open", "damage_sensitive", "fragile", "hot" };

    public static string ToName(FerlObjectType type) => Names[(int)type];

    public static FerlObjectType FromName(string name)
    {
        int index = Array.IndexOf(Names, name);
        return index >= 0 ? (FerlObjectType)index : FerlObjectType.Other;
    }
}
