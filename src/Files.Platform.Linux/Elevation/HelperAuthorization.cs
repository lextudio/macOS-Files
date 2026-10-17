// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Elevation;
using Files.Platform.Posix.Launching;
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Files.Platform.Linux.Elevation
{
	public static class HelperAuthorization
	{
		private static readonly ElevationJsonContext DisplayContext = new(new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		});

		public static string DisplayJson(HelperRequest request)
			=> DisplaySanitizer.Escape(JsonSerializer.Serialize(request, DisplayContext.HelperRequest));

		public static string Summary(HelperRequest request)
		{
			ElevationHelperProtocol.Validate(request);
			return request.Operation + "; sources=" + request.Sources.Length.ToString(CultureInfo.InvariantCulture)
				+ "; first source=" + DisplayPath(request.Sources[0]) + "; target="
				+ (request.Target is null ? "none" : DisplayPath(request.Target));
		}

		private static string DisplayPath(string path)
			=> DisplaySanitizer.Escape(JsonSerializer.Serialize(path, DisplayContext.String));

		public static string[] Arguments(string json)
			=> [Summary(ElevationHelperProtocol.ParseRequest(json)), Digest(new UTF8Encoding(false, true).GetBytes(json))];

		public static string Digest(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

		public static HelperRequest Verify(ReadOnlySpan<byte> data, string[] arguments)
		{
			if (data.Length > ElevationHelperProtocol.MaximumBytes || arguments.Length != 2 || arguments[1] != Digest(data))
				throw new InvalidDataException("Authorization does not match the request.");
			var request = ElevationHelperProtocol.ParseRequest(new UTF8Encoding(false, true).GetString(data));
			if (arguments[0] != Summary(request)) throw new InvalidDataException("Authorization summary does not match the request.");
			return request;
		}
	}
}
