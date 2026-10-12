// Copyright (c) Files Community
// Licensed under the MIT License.

#if DESKTOP_MACOS
using System;
using LeXtudio.DevFlow.Agent.Core;
using LeXtudio.DevFlow.Agent.Uno;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace Files.App
{
	/// <summary>
	/// Starts the in-process DevFlow automation agent (HTTP API on localhost) used to drive and
	/// inspect the macOS build during bring-up. Set <c>FILES_DEVFLOW_AGENT=0</c> to disable.
	/// </summary>
	internal static class DevFlowHost
	{
		private static UnoAgentService? _agent;

		public static void Start()
		{
			if (Environment.GetEnvironmentVariable("FILES_DEVFLOW_AGENT") == "0")
				return;

			var portValue = Environment.GetEnvironmentVariable("DEVFLOW_AGENT_PORT");
			var port = int.TryParse(portValue, out var parsed) && parsed > 0
				? parsed
				: DevFlowAgentPortResolver.GetPortFromAssemblyMetadata() ?? AgentOptions.DefaultPort;

			_agent = new UnoAgentService(new AgentOptions { Port = port });
			_agent.Start();
			Console.Error.WriteLine($"[Files] DevFlow agent listening on http://localhost:{port}");
		}
	}
}
#endif
