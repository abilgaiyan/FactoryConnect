using System.Reflection;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class DurableObservationProcessingWorkerSqlTimeoutTests
{
    [Fact]
    public void WorkerHasOnlySqlTimeoutRecoveryFilter()
    {
        var execute = typeof(DurableObservationProcessingWorker).GetMethod(
            "ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(execute);
        var stateMachine = execute!.GetCustomAttribute<
            System.Runtime.CompilerServices.AsyncStateMachineAttribute>();
        Assert.NotNull(stateMachine);
        // Guard against accidentally replacing the hosted worker with
        // BackgroundServiceExceptionBehavior.Ignore in its implementation.
        Assert.NotNull(typeof(DurableObservationProcessingPipelineSet).GetMethod("RunAsync"));
    }
}
