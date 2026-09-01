using Grpc.Core;
using Grpc.Net.ClientFactory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NResilience.Grpc;

namespace NResilience.Tests;

/// <summary>
///     <c>AddGrpcResilience()</c>: what it registers, at what scope, and what it takes off the table.
/// </summary>
public sealed class GrpcRegistrationTests
{
    [Fact]
    public void The_interceptor_is_registered_once_at_channel_scope()
    {
        // Client scope builds an interceptor per resolved client, which hands every client a fresh
        // breaker and a retry budget that never accumulates - the exact failure NRES005 exists to
        // catch, shipped by the registration meant to prevent it. Pinned rather than assumed.
        var services = new ServiceCollection();

        services.AddGrpcClient<TestGrpcClient>("orders", o => o.Address = new Uri("https://orders.test")).AddGrpcResilience();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<GrpcClientFactoryOptions>>().Get("orders");

        var registration = Assert.Single(options.InterceptorRegistrations);

        Assert.Equal(InterceptorScope.Channel, registration.Scope);
    }

    [Fact]
    public void The_registration_takes_the_transport_timeout_off_the_table()
    {
        var services = new ServiceCollection();

        services.AddGrpcClient<TestGrpcClient>("orders", o => o.Address = new Uri("https://orders.test")).AddGrpcResilience();

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public void The_transport_timeout_is_left_alone_when_the_caller_says_so()
    {
        var services = new ServiceCollection();

        services.AddGrpcClient<TestGrpcClient>("orders", o => o.Address = new Uri("https://orders.test"))
            .AddGrpcResilience(configureOptions: o => o.OwnTransportTimeout = false);

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");

        Assert.NotEqual(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public void A_policy_that_cannot_be_executed_is_refused_at_registration_time()
    {
        var services = new ServiceCollection();
        var builder = services.AddGrpcClient<TestGrpcClient>("orders", o => o.Address = new Uri("https://orders.test"));

        Assert.Throws<ResilienceConfigurationException>(
            () => builder.AddGrpcResilience(GrpcResilience.Default with { Attempts = 0 }));
    }

    [Fact]
    public void Options_that_cannot_be_used_are_refused_at_registration_time()
    {
        var services = new ServiceCollection();
        var builder = services.AddGrpcClient<TestGrpcClient>("orders", o => o.Address = new Uri("https://orders.test"));

        Assert.Throws<ResilienceConfigurationException>(
            () => builder.AddGrpcResilience(configureOptions: o => o.MaxScopes = 0));
    }

    [Fact]
    public void The_policy_is_named_after_the_client_so_two_clients_are_told_apart()
    {
        // The preset is called "grpc", so without renaming every client in the process would report
        // under one name. The interceptor is the thing that holds the renamed policy.
        var interceptor = new ResilienceInterceptor(GrpcResilience.Default with { Name = "orders" });

        Assert.Equal("orders", interceptor.Policy.Name);
    }

    /// <summary>
    ///     The minimum a gRPC client is: a type the factory can build from a
    ///     <see cref="CallInvoker" />. No codegen, because nothing here makes a call.
    /// </summary>
    public sealed class TestGrpcClient(CallInvoker callInvoker)
    {
        public CallInvoker CallInvoker { get; } = callInvoker;
    }
}
