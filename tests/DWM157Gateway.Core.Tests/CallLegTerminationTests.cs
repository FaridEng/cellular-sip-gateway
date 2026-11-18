using DWM157Gateway.Core.Sip;

namespace DWM157Gateway.Core.Tests;

public sealed class CallLegTerminationTests
{
    [Fact]
    public async Task ReportsCellularWhenCellularLegEndsFirst()
    {
        var sipEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cellularEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = CellularSipCallCoordinator.WaitForCallEndAsync(sipEnded.Task, cellularEnded.Task);
        cellularEnded.SetResult();

        Assert.Equal(CellularSipCallCoordinator.CallEndOrigin.Cellular, await result);
    }

    [Fact]
    public async Task ReportsSipWhenSipLegEndsFirst()
    {
        var sipEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cellularEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = CellularSipCallCoordinator.WaitForCallEndAsync(sipEnded.Task, cellularEnded.Task);
        sipEnded.SetResult();

        Assert.Equal(CellularSipCallCoordinator.CallEndOrigin.Sip, await result);
    }
}
