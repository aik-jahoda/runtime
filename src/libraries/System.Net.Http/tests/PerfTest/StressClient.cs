// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace HttpStress
{
    public class StressClient : IDisposable
    {
        private const string UNENCRYPTED_HTTP2_ENV_VAR = "DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP2UNENCRYPTEDSUPPORT";

        private readonly (string name, int requestsCount, Func<RequestContext, int, Task> operation)[] _clientOperations;
        private readonly Uri _baseAddress;
        private readonly Configuration _config;
        private readonly StressResultAggregator _aggregator;
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public StressClient((string name, int requestsCount, Func<RequestContext, int, Task> operation)[] clientOperations, Configuration configuration)
        {
            _clientOperations = clientOperations;
            _config = configuration;
            _baseAddress = new Uri(configuration.ServerUri);
            _aggregator = new StressResultAggregator(clientOperations);
        }

        public async Task Start()
        {
            if (_cts.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(StressClient));
            }

            _stopwatch.Start();
            await StartCore();
            Stop();
        }

        private void Stop()
        {
            _cts.Cancel();
            _stopwatch.Stop();
            _cts.Dispose();
        }

        public void PrintFinalReport()
        {
            lock (Console.Out)
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine("HttpStress Run Final Report");
                Console.WriteLine();

                _aggregator.PrintCurrentResults(_stopwatch.Elapsed);
            }
        }

        public void Dispose() => Stop();

        private async Task StartCore()
        {
            if (_baseAddress.Scheme == "http")
            {
                Environment.SetEnvironmentVariable(UNENCRYPTED_HTTP2_ENV_VAR, "1");
            }

            HttpMessageHandler CreateHttpHandler()
            {
                if (_config.UseWinHttpHandler)
                {
                    return new System.Net.Http.WinHttpHandler()
                    {
                        ServerCertificateValidationCallback = delegate { return true; }
                    };
                }
                else
                {
                    return new SocketsHttpHandler()
                    {
                        PooledConnectionLifetime = _config.ConnectionLifetime.GetValueOrDefault(Timeout.InfiniteTimeSpan),
                        SslOptions = new SslClientAuthenticationOptions
                        {
                            RemoteCertificateValidationCallback = delegate { return true; }
                        }
                    };
                }
            }

            HttpClient CreateHttpClient() =>
                new HttpClient(CreateHttpHandler())
                {
                    BaseAddress = _baseAddress,
                    Timeout = _config.DefaultTimeout,
                    DefaultRequestVersion = _config.HttpVersion,
                };

            using HttpClient client = CreateHttpClient();

            // Before starting the full-blown test, make sure can communicate with the server
            // Needed for scenaria where we're deploying server & client in separate containers, simultaneously.
            await SendTestRequestToServer(maxRetries: 10);

            await RunWorker();

            async Task RunWorker()
            {
                // create random instance specific to the current worker
                var random = new Random(_config.RandomSeed);
                var stopwatch = new Stopwatch();

                for (int i = 0; i < _clientOperations.Length; i++)
                {
                    (string name, int requestsCount, Func<RequestContext, int, Task> func) = _clientOperations[i];

                    if (_cts.IsCancellationRequested)
                        break;

                    for (int j = 0; j < requestsCount; j++)
                    {
                        if (_cts.IsCancellationRequested)
                            break;

                        RequestContext requestContext = new RequestContext(_config, client, random, _cts.Token, 0);
                        stopwatch.Restart();
                        try
                        {
                            await func(requestContext, j);
                        }
                        catch (OperationCanceledException) when (requestContext.IsCancellationRequested || _cts.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            throw;
                        }
                        _aggregator.RecordSuccess(i, j, stopwatch.Elapsed);
                    }

                    _aggregator.PrintLatenciesForOperation(i);
                }
            }

            async Task SendTestRequestToServer(int maxRetries)
            {
                using HttpClient client = CreateHttpClient();
                for (int remainingRetries = maxRetries; ; remainingRetries--)
                {
                    try
                    {
                        await client.GetAsync("/");
                        break;
                    }
                    catch (HttpRequestException e) when (remainingRetries > 0)
                    {
                        Console.WriteLine($"Stress client could not connect to host {_baseAddress}, {remainingRetries} attempts remaining. {e.InnerException?.Message ?? e.Message}");
                        await Task.Delay(millisecondsDelay: 1000);
                    }
                }
            }
        }

        private sealed class StressResultAggregator
        {
            private readonly string[] _operationNames;

            private long _totalRequests = 0;
            private readonly long[] _successes;
            private long _lastTotal = -1;
            private readonly double[][] _latencies;

            public StressResultAggregator((string name, int requestsCount, Func<RequestContext, int, Task>)[] operations)
            {
                _operationNames = operations.Select(x => x.name).ToArray();
                _successes = new long[operations.Length];
                _latencies = new double[operations.Length][];
                for (int i = 0; i < operations.Length; i++)
                {
                    _latencies[i] = new double[operations[i].requestsCount];
                }
            }

            public void RecordSuccess(int operationIndex, int requestIndex, TimeSpan elapsed)
            {
                Interlocked.Increment(ref _totalRequests);
                Interlocked.Increment(ref _successes[operationIndex]);

                _latencies[operationIndex][requestIndex] = elapsed.TotalMilliseconds;
            }

            public void PrintCurrentResults(TimeSpan runtime)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write("[" + DateTime.Now + "]");
                Console.ResetColor();

                if (_lastTotal == _totalRequests)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                }
                _lastTotal = _totalRequests;
                Console.Write(" Total: " + _totalRequests.ToString("N0"));
                Console.ResetColor();
                Console.WriteLine($" Runtime: " + runtime.ToString(@"hh\:mm\:ss"));

                for (int i = 0; i < _operationNames.Length; i++)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write($"\t{_operationNames[i].PadRight(30)}");
                    Console.ResetColor();
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("Success: ");
                    Console.Write(_successes[i].ToString("N0"));
                    Console.ResetColor();
                }

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write("\t    TOTAL".PadRight(31));
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("Success: ");
                Console.Write(_successes.Sum().ToString("N0"));
                Console.ResetColor();

            }

            public void PrintLatenciesForOperation(int operationIndex)
            {
                double[] latencies = _latencies[operationIndex];
                Array.Sort(latencies);

                Console.WriteLine($"Latency(ms): \t n={latencies.Length},\tp50={Pc(0.5)},\tp75={Pc(0.75)},\tp99={Pc(0.99)},\tp999={Pc(0.999)},\tmax={Pc(1)}\t {_operationNames[operationIndex]}");

                double Pc(double percentile)
                {
                    int N = latencies.Length;
                    double n = (N - 1) * percentile + 1;
                    if (n == 1) return Rnd(latencies[0]);
                    else if (n == N) return Rnd(latencies[N - 1]);
                    else
                    {
                        int k = (int)n;
                        double d = n - k;
                        return Rnd(latencies[k - 1] + d * (latencies[k] - latencies[k - 1]));
                    }

                    static double Rnd(double value) => Math.Round(value, 2);
                }
            }
        }


        private class StructuralEqualityComparer<T> : IEqualityComparer<T> where T : IStructuralEquatable
        {
            public bool Equals(T left, T right) => left.Equals(right, StructuralComparisons.StructuralEqualityComparer);
            public int GetHashCode(T value) => value.GetHashCode(StructuralComparisons.StructuralEqualityComparer);
        }
    }
}
