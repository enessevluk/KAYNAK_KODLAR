namespace CustomGenerator.Patches
{
    // Intentionally no Harmony patch on GenerateRiverLayout.Process.
    //
    // River enable/disable is applied before world processing by Timing_Start
    // through World.Config.Rivers. When rivers are enabled, Rust's native
    // generator must own the complete path -> terrain carve -> water mesh chain.
    // Re-emitting this method through an identity transpiler made Raven dependent
    // on the exact IL layout of each Rust update and provided no behavior.
    internal static class RiverGenerationPolicy
    {
        internal const string Mode = "native_pipeline_unpatched";
    }
}
