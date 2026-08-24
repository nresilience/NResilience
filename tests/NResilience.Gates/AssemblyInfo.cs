using Xunit.Sdk;
using Xunit.v3;

// The suspending-path measurements read a process-wide allocation counter, because a
// continuation resumed on a thread-pool thread does not allocate against the thread that
// started the operation. A process-wide counter is only meaningful in a quiet process, so
// nothing in this assembly may run concurrently with anything else in it.
[assembly: Parallelization(Mode = ParallelMode.None)]
