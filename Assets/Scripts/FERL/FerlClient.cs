using System;

// Identifies this Unity process to the bridge, which lets one client (headset or editor) own
// the session at a time instead of two clients rebuilding the world under each other.
public static class FerlClient
{
    public static readonly string Id = Guid.NewGuid().ToString("N");
}
