using UnityEngine;

public static class TransformSearchExtensions
{
    public static Transform FindDescendant(this Transform root, string childName)
    {
        if (root == null)
        {
            return null;
        }

        foreach (Transform child in root)
        {
            if (child.name == childName)
            {
                return child;
            }

            Transform match = child.FindDescendant(childName);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }
}
