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

                    GC.Collect();
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
                        catch (Exception e)
                        {
                            Console.WriteLine(e);
                            throw;
                        }
                        _aggregator.RecordSuccess(i, j, stopwatch.Elapsed);
                    }

                    _aggregator.PrintLatenciesForOperation(i);
                }
            }

            async Task SendTestRequestToServer(int maxRetries)
            {
                //using HttpClient client = CreateHttpClient();
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

            public void PrintLatenciesForOperation(int operationIndex)
            {
                double[] latencies = _latencies[operationIndex];
                Array.Sort(latencies);

                Console.WriteLine($"Latency(ms): \t n={latencies.Length},\tp50={Pc(0.5),6:F3},\tp75={Pc(0.75),6:F3},\tp99={Pc(0.99),6:F3},\tp999={Pc(0.999),6:F3},\tmax={Pc(1),6:N3}\t {_operationNames[operationIndex]}");

                int twentyPercent = (int)(latencies.Length*0.2);
                Console.WriteLine($"Latency(ms): \t n={latencies.Length},\tp50={(latencies.Skip(twentyPercent).Take(latencies.Length - 2 * twentyPercent).Sum() / latencies.Length),6:F3}");



                double Pc(double percentile)
                {
                    int N = latencies.Length;
                    double n = (N - 1) * percentile + 1;
                    if (n == 1) return latencies[0];
                    else if (n == N) return latencies[N - 1];
                    else
                    {
                        int k = (int)n;
                        double d = n - k;
                        return latencies[k - 1] + d * (latencies[k] - latencies[k - 1]);
                    }
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
