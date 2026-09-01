using System;
using System.Collections.Generic;
using System.Threading;
using GoingCooperative.Core;

internal static class MultiPeerUdpTransportTests
{
    public static int Run()
    {
        var failures = 0;
        if (!MultiplayerLifecyclePolicy.ShouldApplyDisconnectedPeerCleanup(false))
        {
            Fail("disconnect cleanup should run when no replacement peer is connected", ref failures);
        }
        if (MultiplayerLifecyclePolicy.ShouldApplyDisconnectedPeerCleanup(true))
        {
            Fail("disconnect cleanup must not remove a replacement peer using the same slot", ref failures);
        }

        var sessionCode = DirectTransportSecurity.GenerateSessionCode();
        var host = new UdpNetworkTransport(true, sessionCode);
        var clients = new List<UdpNetworkTransport>();
        UdpNetworkTransport? duplicate = null;

        try
        {
            host.StartHost(0);
            var port = host.LocalPort;
            if (port <= 0)
            {
                Fail("multi-peer UDP host did not bind a port", ref failures);
                return failures;
            }

            for (var i = 1; i <= 3; i++)
            {
                var client = new UdpNetworkTransport(true, sessionCode);
                client.Connect("127.0.0.1", port);
                clients.Add(client);
            }

            for (var i = 0; i < clients.Count; i++)
            {
                if (!WaitUntil(
                        () => clients[i].AuthenticationEstablished,
                        5000))
                {
                    Fail(
                        "multi-peer UDP client "
                            + (i + 1).ToString()
                            + " authentication timed out",
                        ref failures);
                    return failures;
                }
            }

            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                "client-1",
                "client-2",
                "client-3"
            };
            for (var i = 0; i < clients.Count; i++)
            {
                var peerId = "client-" + (i + 1).ToString();
                clients[i].Send(
                    new TransportEnvelope(
                        TransportMessageKind.ReplicationHello,
                        0L,
                        peerId,
                        "bind-" + peerId));
            }

            var receivedHelloIds = new HashSet<string>(StringComparer.Ordinal);
            if (!WaitUntil(
                    () =>
                    {
                        Drain(
                            host,
                            envelope =>
                            {
                                if (envelope.Kind
                                        == TransportMessageKind.ReplicationHello)
                                {
                                    receivedHelloIds.Add(envelope.SenderId);
                                }
                            });
                        return receivedHelloIds.SetEquals(expected);
                    },
                    5000))
            {
                Fail(
                    "multi-peer UDP host did not bind all client hello IDs",
                    ref failures);
                return failures;
            }

            for (var i = 0; i < clients.Count; i++)
            {
                var peerId = "client-" + (i + 1).ToString();
                host.EnableBinarySecurityData(peerId);
                clients[i].EnableBinarySecurityData();
            }

            if (host.BoundPeerCount != 3)
            {
                Fail(
                    "multi-peer UDP bound peer count expected=3 actual="
                        + host.BoundPeerCount.ToString(),
                    ref failures);
            }

            DrainAll(clients);

            host.Send(
                new TransportEnvelope(
                    TransportMessageKind.Ack,
                    1L,
                    MultiplayerPeerIds.Host,
                    "broadcast-1"));
            for (var i = 0; i < clients.Count; i++)
            {
                var index = i;
                if (!WaitForPayload(
                        clients[index],
                        "broadcast-1",
                        3000))
                {
                    Fail(
                        "multi-peer UDP broadcast missing client-"
                            + (index + 1).ToString(),
                        ref failures);
                }
            }

            DrainAll(clients);

            host.SendToPeer(
                "client-2",
                new TransportEnvelope(
                    TransportMessageKind.Ack,
                    2L,
                    MultiplayerPeerIds.Host,
                    "target-client-2"));
            if (!WaitForPayload(
                    clients[1],
                    "target-client-2",
                    3000))
            {
                Fail(
                    "multi-peer UDP targeted send missing client-2",
                    ref failures);
            }

            Thread.Sleep(150);
            if (TryFindPayload(clients[0], "target-client-2")
                || TryFindPayload(clients[2], "target-client-2"))
            {
                Fail(
                    "multi-peer UDP targeted send leaked to another client",
                    ref failures);
            }

            DrainAll(clients);

            host.SendToAllExcept(
                "client-2",
                new TransportEnvelope(
                    TransportMessageKind.Ack,
                    3L,
                    MultiplayerPeerIds.Host,
                    "all-except-client-2"));
            if (!WaitForPayload(
                    clients[0],
                    "all-except-client-2",
                    3000)
                || !WaitForPayload(
                    clients[2],
                    "all-except-client-2",
                    3000))
            {
                Fail(
                    "multi-peer UDP excluded broadcast missing an allowed client",
                    ref failures);
            }

            Thread.Sleep(150);
            if (TryFindPayload(
                    clients[1],
                    "all-except-client-2"))
            {
                Fail(
                    "multi-peer UDP excluded broadcast reached client-2",
                    ref failures);
            }

            duplicate = new UdpNetworkTransport(true, sessionCode);
            duplicate.Connect("127.0.0.1", port);
            if (!WaitUntil(
                    () => duplicate.AuthenticationEstablished,
                    5000))
            {
                Fail(
                    "multi-peer UDP duplicate client authentication timed out",
                    ref failures);
            }
            else
            {
                var beforeBindingFailures = host.PeerBindingFailures;
                duplicate.Send(
                    new TransportEnvelope(
                        TransportMessageKind.ReplicationHello,
                        0L,
                        "client-2",
                        "duplicate-client-2"));
                if (!WaitUntil(
                        () =>
                        {
                            Drain(host, _ => { });
                            return host.PeerBindingFailures
                                > beforeBindingFailures;
                        },
                        3000))
                {
                    Fail(
                        "multi-peer UDP duplicate peer ID was not rejected",
                        ref failures);
                }

                if (host.BoundPeerCount != 3)
                {
                    Fail(
                        "multi-peer UDP duplicate peer changed bound count actual="
                            + host.BoundPeerCount.ToString(),
                        ref failures);
                }
            }
        }
        catch (Exception ex)
        {
            Fail(
                "multi-peer UDP test threw "
                    + ex.GetType().Name
                    + ":"
                    + ex.Message,
                ref failures);
        }
        finally
        {
            if (duplicate != null)
            {
                duplicate.Stop();
            }

            for (var i = 0; i < clients.Count; i++)
            {
                clients[i].Stop();
            }

            host.Stop();
        }

        if (failures == 0)
        {
            Console.WriteLine("PASS MultiPeerUdpTransport");
        }

        return failures;
    }

    private static bool WaitForPayload(
        UdpNetworkTransport transport,
        string payload,
        int timeoutMs)
    {
        return WaitUntil(
            () => TryFindPayload(transport, payload),
            timeoutMs);
    }

    private static bool TryFindPayload(
        UdpNetworkTransport transport,
        string payload)
    {
        var found = false;
        Drain(
            transport,
            envelope =>
            {
                if (string.Equals(
                        envelope.Payload,
                        payload,
                        StringComparison.Ordinal))
                {
                    found = true;
                }
            });
        return found;
    }

    private static void DrainAll(
        IReadOnlyList<UdpNetworkTransport> transports)
    {
        for (var i = 0; i < transports.Count; i++)
        {
            Drain(transports[i], _ => { });
        }
    }

    private static void Drain(
        UdpNetworkTransport transport,
        Action<TransportEnvelope> visitor)
    {
        var budget = 128;
        while (budget-- > 0
            && transport.TryReceive(out var envelope))
        {
            visitor(envelope);
        }
    }

    private static bool WaitUntil(
        Func<bool> condition,
        int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    private static void Fail(
        string message,
        ref int failures)
    {
        Console.Error.WriteLine("FAIL " + message);
        failures++;
    }
}
