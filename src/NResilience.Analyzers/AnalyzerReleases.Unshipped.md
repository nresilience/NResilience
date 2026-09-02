; Unshipped analyzer release ; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

 Rule ID | Category    | Severity | Notes                                                         
---------|-------------|----------|---------------------------------------------------------------
 NRES001 | Reliability | Warning  | The attempt's cancellation token is not passed to the work.   
 NRES002 | Reliability | Warning  | A different cancellation token is passed inside the callback. 
 NRES003 | Usage       | Warning  | The policy will not pass validation.                          
 NRES004 | Usage       | Warning  | AttemptTimeout is longer than Deadline.                       
 NRES005 | Reliability | Warning  | A breaker, retry budget, policy scope or gRPC interceptor is created per call. 
 NRES006 | Reliability | Info     | A resilient HttpClient is created per call.                   
 NRES007 | Performance | Info     | The callback does not need to be async.                       
 NRES008 | Reliability | Info     | A policy configuring Hedge or Timeouts is created per call.   
