using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace NResilience.Tests;

/// <summary>What counts as a failure, said once.</summary>
public sealed class ClassifierTests
{
    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(SocketException))]
    public void Default_treats_the_known_transient_families_as_transient(Type type)
    {
        var exception = (Exception)Activator.CreateInstance(type)!;
        Assert.Equal(VerdictKind.Transient, Classifier.Default.ClassifyException(exception).Kind);
    }

    [Fact]
    public void Default_treats_anything_unrecognised_as_permanent()
    {
        Assert.Equal(VerdictKind.Permanent, Classifier.Default.ClassifyException(new InvalidOperationException()).Kind);
        Assert.Equal(VerdictKind.Permanent, Classifier.Default.ClassifyException(new ArgumentNullException()).Kind);
    }

    [Fact]
    public void RetryEverything_treats_anything_as_transient()
    {
        Assert.Equal(VerdictKind.Transient, Classifier.RetryEverything.ClassifyException(new InvalidOperationException()).Kind);
    }

    [Fact]
    public void A_rule_you_add_beats_the_one_it_was_derived_from()
    {
        Classifier classifier = Classifier.Default.On<IOException>(Verdict.Permanent);

        Assert.Equal(VerdictKind.Permanent, classifier.ClassifyException(new IOException()).Kind);
        Assert.Equal(VerdictKind.Transient, Classifier.Default.ClassifyException(new IOException()).Kind);
    }

    [Fact]
    public void Rules_match_subclasses()
    {
        Classifier classifier = Classifier.Default.On<InvalidOperationException>(Verdict.Transient);
        Assert.Equal(VerdictKind.Transient, classifier.ClassifyException(new ObjectDisposedException("x")).Kind);
    }

    [Fact]
    public void A_predicate_rule_can_inspect_the_exception()
    {
        Classifier classifier = Classifier.Default.On<InvalidOperationException>(
            static e => e.Message == "retry" ? Verdict.Transient : Verdict.Permanent);

        Assert.Equal(VerdictKind.Transient, classifier.ClassifyException(new InvalidOperationException("retry")).Kind);
        Assert.Equal(VerdictKind.Permanent, classifier.ClassifyException(new InvalidOperationException("no")).Kind);
    }

    [Fact]
    public void An_unregistered_result_type_is_always_a_success()
    {
        Assert.Equal(VerdictKind.Ok, Classifier.Default.ClassifyResult(42).Kind);
        Assert.Equal(VerdictKind.Ok, Classifier.Http.ClassifyResult(42).Kind);
    }

    [Fact]
    public void Result_rules_are_matched_by_exact_type_and_cached_per_type()
    {
        Classifier classifier = Classifier.Default.OnResult<int>(static v => v < 0 ? Verdict.Transient : Verdict.Ok);

        // Alternating result types exercises the per-type cache in both directions.
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(VerdictKind.Transient, classifier.ClassifyResult(-1).Kind);
            Assert.Equal(VerdictKind.Ok, classifier.ClassifyResult("anything").Kind);
        }
    }

    [Fact]
    public void Two_classifiers_do_not_share_a_result_cache()
    {
        Classifier a = Classifier.Default.OnResult<int>(static _ => Verdict.Transient);
        Classifier b = Classifier.Default.OnResult<int>(static _ => Verdict.Permanent);

        Assert.Equal(VerdictKind.Transient, a.ClassifyResult(1).Kind);
        Assert.Equal(VerdictKind.Permanent, b.ClassifyResult(1).Kind);
        Assert.Equal(VerdictKind.Transient, a.ClassifyResult(1).Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, VerdictKind.Ok)]
    [InlineData(HttpStatusCode.NotFound, VerdictKind.Ok)]
    [InlineData(HttpStatusCode.BadRequest, VerdictKind.Ok)]
    [InlineData(HttpStatusCode.Unauthorized, VerdictKind.Ok)]
    [InlineData(HttpStatusCode.RequestTimeout, VerdictKind.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, VerdictKind.Transient)]
    [InlineData(HttpStatusCode.BadGateway, VerdictKind.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, VerdictKind.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, VerdictKind.Throttled)]
    public void Http_classifies_status_codes_the_way_a_careful_engineer_would(HttpStatusCode status, VerdictKind expected)
    {
        using var response = new HttpResponseMessage(status);
        Assert.Equal(expected, Classifier.Http.ClassifyResult(response).Kind);
    }

    [Fact]
    public void Http_honours_Retry_After_on_a_429()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));

        Verdict verdict = Classifier.Http.ClassifyResult(response);

        Assert.Equal(VerdictKind.Throttled, verdict.Kind);
        Assert.Equal(TimeSpan.FromSeconds(7), verdict.RetryAfter);
    }

    [Fact]
    public void A_503_with_Retry_After_is_throttling_rather_than_a_transient_fault()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));

        Verdict verdict = Classifier.Http.ClassifyResult(response);

        Assert.Equal(VerdictKind.Throttled, verdict.Kind);
        Assert.Equal(TimeSpan.FromSeconds(3), verdict.RetryAfter);
    }

    [Fact]
    public void Http_treats_a_transport_failure_as_transient()
    {
        Assert.Equal(VerdictKind.Transient, Classifier.Http.ClassifyException(new HttpRequestException()).Kind);
    }

    [Fact]
    public void The_active_ruleset_is_printable()
    {
        string dump = Classifier.Http.ToString();

        Assert.Contains("HttpRequestException", dump, StringComparison.Ordinal);
        Assert.Contains("HttpResponseMessage", dump, StringComparison.Ordinal);
        Assert.Contains("any other exception -> Permanent", dump, StringComparison.Ordinal);
    }
}
