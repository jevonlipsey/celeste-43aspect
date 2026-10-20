using Mono.Cecil;

namespace Celeste43.Verify;

/// runs the verifier layers in order against an open module. the patcher gates its output on this.
public static class VerifyRunner
{
    // helperPayload is the Fit43.dll the patcher embedded; see CelesteChecks.
    public static void Run(Checks checks, ModuleDefinition module, byte[]? helperPayload)
    {
        new IlVerifier(checks, module).Run();
        new CelesteChecks(checks, module, helperPayload).Run();
    }
}
