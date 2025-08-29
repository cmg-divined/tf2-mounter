using System;
using System.Collections.Generic;
using IO = System.IO;

internal static class TF2Vmt
{
	public sealed class Data
	{
		public string Shader;
		public Dictionary<string,string> Kv = new(StringComparer.OrdinalIgnoreCase);
	}

	public static Data Parse( IO.Stream stream )
	{
		var data = new Data();
		using var sr = new IO.StreamReader( stream );
		string line;
		while ( (line = sr.ReadLine()) != null )
		{
			line = line.Trim();
			if (line.Length == 0) continue;
			if (line.StartsWith("//")) continue;
			if (line == "{" || line == "}") continue; // Skip braces
			
			// Extract shader name - first quoted string
			if (line.StartsWith("\"") && data.Shader == null)
			{
				int q = line.IndexOf('"', 1);
				if (q > 1) data.Shader = line.Substring(1, q-1);
				continue;
			}
			
			// Parse key-value pairs - handle multiple formats
			string key = null;
			string value = null;
			
			// Format 1: "key" "value"
			if (line.StartsWith("\""))
			{
				int q1 = line.IndexOf('"', 1);
				if (q1 > 1)
				{
					key = line.Substring(1, q1-1);
					var remaining = line.Substring(q1+1).Trim();
					
					if (remaining.StartsWith("\""))
					{
						int q2e = remaining.IndexOf('"', 1);
						if (q2e > 0)
						{
							value = remaining.Substring(1, q2e-1);
						}
					}
					else
					{
						// Format 2: "key" unquoted_value
						value = remaining.Trim();
					}
				}
			}
			// Format 3: unquoted_key "value" or unquoted_key unquoted_value
			else if (line.Contains(" ") || line.Contains("\t"))
			{
				var parts = line.Split(new char[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length >= 2)
				{
					key = parts[0].Trim();
					value = parts[1].Trim();
					
					// Remove quotes from value if present
					if (value.StartsWith("\"") && value.EndsWith("\"") && value.Length >= 2)
					{
						value = value.Substring(1, value.Length - 2);
					}
				}
			}
			
			// Add to dictionary if we found both key and value
			if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
			{
				data.Kv[key] = value;
			}
		}
		return data;
	}
}
