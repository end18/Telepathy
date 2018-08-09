﻿using System;
using UnityEngine;
using System.Threading;

public class HUD : MonoBehaviour
{
    Telepathy.Client client = new Telepathy.Client();
    Telepathy.Server server = new Telepathy.Server();

    [Header("Stress test")]
    public int packetsPerTick = 1000;
    public byte[] stressBytes = new byte[]{0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01, 0xAF, 0xFE, 0x01};
    bool stressTestRunning = false;

    void Awake()
    {
        // update even if window isn't focused, otherwise we don't receive.
        Application.runInBackground = true;

        // use Debug.Log functions for TCP so we can see it in the console
        Logger.LogMethod = Debug.Log;
        Logger.LogWarningMethod = Debug.LogWarning;
        Logger.LogErrorMethod = Debug.LogError;
    }

    void Update()
    {
        if (client.Connected)
        {
            if (Input.GetKeyDown(KeyCode.Space))
            {
                client.Send(new byte[]{0xAF, 0xFE});
                client.Send(new byte[]{0xBA, 0xBE});
                //client.Send(stressBytes);
            }

            if (Input.GetKeyDown(KeyCode.S))
            {
                stressTestRunning = !stressTestRunning;
                if (stressTestRunning)
                    Debug.Log("client start stress test with: " + packetsPerTick + " packets per tick");
            }

            // SPAM
            if (stressTestRunning)
            {
                for (int i = 0; i < packetsPerTick; ++i)
                    client.Send(stressBytes);
            }

            // any new message?
            Telepathy.EventType eventType;
            byte[] data;
            if (client.GetNextMessage(out eventType, out data))
            {
                Debug.Log("received event=" + eventType + " msg: " + (data != null ? BitConverter.ToString(data) : "null"));
            }
        }

        if (server.Active)
        {
            if (Input.GetKeyDown(KeyCode.Space))
            {
                server.Send(0, new byte[]{0xAF, 0xFE});
                server.Send(0, new byte[]{0xBA, 0xBE});
            }

            // any new message?
            // -> calling it once per frame is okay, but really why not just
            //    process all messages and make it empty..
            byte[] data;
            Telepathy.EventType eventType;
            uint connectionId;
            int receivedCount = 0;
            while (server.GetNextMessage(out connectionId, out eventType, out data))
            {
                Debug.Log("received connectionId=" + connectionId + " event=" + eventType + " msg: " + (data != null ? BitConverter.ToString(data) : "null"));
                ++receivedCount;
            }
            if (receivedCount > 0) Debug.Log("Server received " + receivedCount + " messages this frame."); // easier on CPU to log this way
        }
    }

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(0, 0, 300, 300));

        // client
        GUILayout.BeginHorizontal();
        GUI.enabled = !client.Connected;
        if (GUILayout.Button("Connect Client"))
        {
            client.Connect("localhost", 1337);
        }
        GUI.enabled = client.Connected;
        if (GUILayout.Button("Disconnect Client"))
        {
            client.Disconnect();
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        // server
        GUILayout.BeginHorizontal();
        GUI.enabled = !server.Active;
        if (GUILayout.Button("Start Server"))
        {
            server.Start("localhost", 1337);
        }
        GUI.enabled = server.Active;
        if (GUILayout.Button("Stop Server"))
        {
            server.Stop();
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        GUILayout.EndArea();
    }

    void OnApplicationQuit()
    {
        // the client/server threads won't receive the OnQuit info if we are
        // running them in the Editor. they would only quit when we press Play
        // again later. this is fine, but let's shut them down here for consistency
        client.Disconnect();
        server.Stop();
    }
}
