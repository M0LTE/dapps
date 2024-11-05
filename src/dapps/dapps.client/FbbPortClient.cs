﻿using Microsoft.Extensions.Logging;
using System.Net.Sockets;

namespace dapps.client;

public class FbbPortClient(string host, int port, ILoggerFactory loggerFactory)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<FbbPortClient>();
    private readonly TcpClient client = new();
    protected NetworkStream? networkStream;

    public BpqSessionState State { get; private set; } = BpqSessionState.PreLogin;

    /// <summary>
    /// Connect and log in to a local BPQ node's FBB port via TCP/IP
    /// </summary>
    /// <param name="user"></param>
    /// <param name="password"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task<bool> FbbLogin(string user, string password)
    {
        if (State != BpqSessionState.PreLogin)
        {
            throw new InvalidOperationException("Already logged in");
        }

        if (0 == port)
        {
            throw new InvalidOperationException("BpqFbbPort is not configured");
        }

        logger.LogInformation("Connecting to BPQ: {user}@{node}:{port}", user, host, port);

        await client.ConnectAsync(host, port);
        client.ReceiveTimeout = 50000;
        networkStream = client.GetStream();

        await networkStream.WriteUtf8AndFlush($"{user}\r{password}\rBPQTERMTCP\r"); // yes, \r

        var (success, _) = networkStream.Expect("Connected to TelnetServer\r"); // lies, it's not telnet

        if (!success)
        {
            return false;
        }

        State = BpqSessionState.LoggedIn;
        return true;
    }

    /// <summary>
    /// Execute a sequence of commands, expect some prompt. Supports a PAUSE nnn command to wait a number of milliseconds.
    /// </summary>
    /// <param name="connectScript"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public async Task<bool> ExecuteScript(string[] connectScript, string expectedPrompt)
    {
        State.AssertIsLoggedInToLocalBpq();

        logger.LogInformation("Executing script: {0}", string.Join(", ", connectScript));

        foreach (var scriptLine in connectScript)
        {
            if (scriptLine.StartsWith("PAUSE ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = scriptLine.Split(" ");
                if (parts.Length == 2 && int.TryParse(parts[1], out var pause))
                {
                    await Task.Delay(pause);
                }
                else
                {
                    logger.LogWarning("Could not parse PAUSE command: '{0}'", scriptLine);
                    return false;
                }
            }

            await networkStream!.WriteUtf8AndFlush(scriptLine + "\r"); // yes, \r
        }

        var (gotPrompt, _) = networkStream!.Expect(expectedPrompt);

        return gotPrompt;
    }
}

public enum BpqSessionState
{
    PreLogin, LoggedIn,
}