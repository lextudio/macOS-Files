// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Files.Platform.Abstractions.Elevation
{
	public sealed record HelperRequest(int Version, string Operation, string[] Sources, string? Target);
	public sealed record HelperItemResult(string Source, bool Succeeded, string Error);
	public sealed record HelperResponse(int Version, HelperItemResult[] Items, string Error);

	[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
	[JsonSerializable(typeof(string))]
	[JsonSerializable(typeof(HelperRequest))]
	[JsonSerializable(typeof(HelperResponse))]
	public partial class ElevationJsonContext : JsonSerializerContext { }

	public static class ElevationHelperProtocol
	{
		public const string HelperPath = "/usr/lib/desktopfiles/files-elevation-helper";
		public const string PolicyPath = "/usr/share/polkit-1/actions/io.github.memergamer.LinuxFiles.root-actions.policy";
		public const int MaximumBytes = 65536;
		public const int MaximumItems = 64;
		public const int MaximumResultBytes = 524288;

		public static string Serialize(HelperRequest request)
			=> JsonSerializer.Serialize(request, ElevationJsonContext.Default.HelperRequest);

		public static string Serialize(HelperResponse response)
			=> JsonSerializer.Serialize(response, ElevationJsonContext.Default.HelperResponse);

		public static HelperRequest ParseRequest(string json)
		{
			ValidateJson(json);
			using var document = JsonDocument.Parse(json);
			RequireProperties(document.RootElement, "version", "operation", "sources", "target");
			var request = JsonSerializer.Deserialize(json, ElevationJsonContext.Default.HelperRequest) ?? throw new InvalidDataException("Missing plan.");
			Validate(request);
			return request;
		}

		public static HelperResponse ParseResponse(string json)
		{
			ValidateJson(json, MaximumResultBytes);
			using var document = JsonDocument.Parse(json);
			RequireProperties(document.RootElement, "version", "items", "error");
			foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
				RequireProperties(item, "source", "succeeded", "error");
			var response = JsonSerializer.Deserialize(json, ElevationJsonContext.Default.HelperResponse) ?? throw new InvalidDataException("Missing results.");
			if (response.Version != 1 || response.Items is null || response.Items.Length > MaximumItems || response.Error is null
				|| response.Items.Any(item => item is null || item.Source is null || item.Error is null || (item.Succeeded && item.Error.Length != 0)))
				throw new InvalidDataException("Invalid results.");
			return response;
		}

		public static void Validate(HelperRequest request)
		{
			if (request.Version != 1 || request.Operation is not ("delete" or "copy" or "move" or "rename")
				|| request.Sources is null || request.Sources.Length is < 1 or > MaximumItems)
				throw new InvalidDataException("Invalid plan.");
			foreach (var source in request.Sources)
			{
				ValidatePath(source);
				if (request.Operation is "delete" or "move" or "rename" && source is
					"/usr" or "/etc" or "/boot" or "/bin" or "/lib" or "/lib32" or "/lib64" or "/libx32" or "/sbin" or
					"/var" or "/home" or "/root" or "/proc" or "/sys" or "/dev" or "/run" or "/srv" or "/opt" or "/mnt" or "/media" or "/tmp")
					throw new InvalidDataException("Protected system directory.");
			}
			if (request.Sources.Distinct(StringComparer.Ordinal).Count() != request.Sources.Length)
				throw new InvalidDataException("Duplicate source.");
			foreach (var source in request.Sources)
				if (request.Sources.Any(other => other != source && other.StartsWith(source + "/", StringComparison.Ordinal)))
					throw new InvalidDataException("Overlapping sources.");
			if (request.Operation == "delete")
			{
				if (request.Target is not null) throw new InvalidDataException("Unexpected target.");
			}
			else
			{
				ValidatePath(request.Target);
				if (request.Operation is "copy" or "move" && new[] { "/proc", "/sys", "/dev" }.Any(path =>
					request.Target == path || request.Target!.StartsWith(path + "/", StringComparison.Ordinal)))
					throw new InvalidDataException("Virtual filesystem destination refused.");
				if (request.Operation == "rename")
				{
					if (request.Sources.Length != 1 || Path.GetDirectoryName(request.Sources[0]) != Path.GetDirectoryName(request.Target))
						throw new InvalidDataException("Rename must stay in its parent.");
				}
				else if (request.Sources.Any(source => request.Target == source || request.Target!.StartsWith(source + "/", StringComparison.Ordinal))
					|| request.Sources.Select(Path.GetFileName).Distinct(StringComparer.Ordinal).Count() != request.Sources.Length)
					throw new InvalidDataException("Invalid copy destination.");
			}
		}

		public static void ValidatePath(string? path)
		{
			if (string.IsNullOrEmpty(path) || path.Length > 4096 || path[0] != '/' || path == "/" || path.Contains('\0')
				|| path.Split('/').Skip(1).Any(part => part is "" or "." or ".."))
				throw new InvalidDataException("A normalized absolute non-root path is required.");
			if (new System.Text.UTF8Encoding(false, true).GetByteCount(path) > 4096)
				throw new InvalidDataException("Path exceeds UTF-8 length limit.");
		}

		private static void ValidateJson(string json, int maximumBytes = MaximumBytes)
		{
			if (System.Text.Encoding.UTF8.GetByteCount(json) > maximumBytes) throw new InvalidDataException("Plan or results too large.");
			using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
			Check(document.RootElement);
			static void Check(JsonElement element)
			{
				if (element.ValueKind == JsonValueKind.Object)
				{
					var keys = new HashSet<string>(StringComparer.Ordinal);
					foreach (var property in element.EnumerateObject())
					{
						if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
						Check(property.Value);
					}
				}
				else if (element.ValueKind == JsonValueKind.Array)
					foreach (var child in element.EnumerateArray()) Check(child);
			}
		}

		private static void RequireProperties(JsonElement element, params string[] names)
		{
			if (element.ValueKind != JsonValueKind.Object || !element.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(names.Order()))
				throw new InvalidDataException("Unexpected or missing JSON properties.");
		}
	}
}
