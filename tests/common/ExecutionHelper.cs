using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Diagnostics;

using NUnit.Framework;
using Xamarin.Utils;

namespace Xamarin.Tests
{
	class ToolMessage
	{
		public bool IsError;
		public bool IsWarning { get { return !IsError; } }
		public string Prefix;
		public int Number;
		public string PrefixedNumber { get { return Prefix + Number.ToString (); } }
		public string Message;
		public string FileName;
		public int LineNumber;

		public override string ToString ()
		{
			if (string.IsNullOrEmpty (FileName)) {
				return String.Format ("{0} {3}{1:0000}: {2}", IsError ? "error" : "warning", Number, Message, Prefix);
			} else {
				return String.Format ("{3}({4}): {0} {5}{1:0000}: {2}", IsError ? "error" : "warning", Number, Message, FileName, LineNumber, Prefix);
			}
		}
	}

	abstract class Tool
	{
		StringBuilder output = new StringBuilder ();

		List<string> output_lines;

		List<ToolMessage> messages = new List<ToolMessage> ();

		public Dictionary<string, string> EnvironmentVariables { get; set; }
		public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds (60);
#pragma warning disable 0649 // Field 'X' is never assigned to, and will always have its default value Y
		public string WorkingDirectory;
#pragma warning restore 0649

		public IEnumerable<ToolMessage> Messages { get { return messages; } }
		public List<string> OutputLines {
			get {
				if (output_lines == null) {
					output_lines = new List<string> ();
					output_lines.AddRange (output.ToString ().Split ('\n'));
				}
				return output_lines;
			}
		}

		public StringBuilder Output {
			get {
				return output;
			}
		}

		public int Execute (IList<string> arguments)
		{
			return Execute (ToolPath, arguments, false);
		}

		public int Execute (IList<string>  arguments, bool always_show_output)
		{
			return Execute (ToolPath, arguments, always_show_output);
		}

		public int Execute (string toolPath, IList<string> arguments)
		{
			return Execute (toolPath, arguments, false);
		}

		public int Execute (string toolPath, IList<string> arguments, bool always_show_output)
		{
			output.Clear ();
			output_lines = null;

			var args = new List<string> ();
			args.Add ("-t");
			args.Add ("--");
			args.Add (toolPath);
			args.AddRange (arguments);
			var rv = ExecutionHelper.Execute (Configuration.XIBuildPath, args, EnvironmentVariables, output, output, workingDirectory: WorkingDirectory);

			if ((rv != 0 || always_show_output) && output.Length > 0)
				Console.WriteLine ("\t" + output.ToString ().Replace ("\n", "\n\t"));

			ParseMessages ();

			return rv;
		}

		static bool IndexOfAny (string line, out int start, out int end, params string [] values)
		{
			foreach (var value in values) {
				start = line.IndexOf (value, StringComparison.Ordinal);
				if (start >= 0) {
					end = start + value.Length;
					return true;
				}
			}
			start = -1;
			end = -1;
			return false;
		}

		static string RemovePathAtEnd (string line)
		{
			if (line.TrimEnd ().EndsWith ("]", StringComparison.Ordinal)) {
				var start = line.LastIndexOf ("[", StringComparison.Ordinal);
				if (start >= 0) {
					// we want to get the space before `[` too.
					if (start > 0 && line [start - 1] == ' ')
						start --;

					line = line.Substring (0, start);
					return line;
				}
			}

			return line;
		}

		public static List<ToolMessage> ParseMessages (string [] lines, string messageToolName)
		{
			var messages = new List<ToolMessage> ();
			ParseMessages (messages, lines, messageToolName);
			return messages;
		}

		public static void ParseMessages (List<ToolMessage> messages, string [] lines, string messageToolName)
		{
			foreach (var l in lines) {
				var line = l;
				var msg = new ToolMessage ();
				var origin = string.Empty;

				if (IndexOfAny (line, out var idxError, out var endError, ": error ", ":  error ")) {
					msg.IsError = true;
					origin = line.Substring (0, idxError);
					line = line.Substring (endError);
					line = RemovePathAtEnd (line);
				} else if (IndexOfAny (line, out var idxWarning, out var endWarning, ": warning ", ":  warning ")) {
					origin = line.Substring (0, idxWarning);
					line = line.Substring (endWarning);
					line = RemovePathAtEnd (line);
				} else if (line.StartsWith ("error ", StringComparison.Ordinal)) {
					msg.IsError = true;
					line = line.Substring (6);
				} else if (line.StartsWith ("warning ", StringComparison.Ordinal)) {
					msg.IsError = false;
					line = line.Substring (8);
				} else {
					// something else
					continue;
				}
				if (line.Length < 7)
					continue; // something else

				msg.Prefix = line.Substring (0, 2);
				if (!int.TryParse (line.Substring (2, 4), out msg.Number))
					continue; // something else

				line = line.Substring (8);
				var toolName = messageToolName;
				if (toolName != null && line.StartsWith (toolName + ": ", StringComparison.Ordinal))
					line = line.Substring (toolName.Length + 2);

				msg.Message = line;

				if (!string.IsNullOrEmpty (origin)) {
					var idx = origin.IndexOf ('(');
					if (idx > 0) {
						var closing = origin.IndexOf (')');
						var number = 0;
						if (!int.TryParse (origin.Substring (idx + 1, closing - idx - 1), out number))
							continue;
						msg.LineNumber = number;
						msg.FileName = origin.Substring (0, idx);
					} else {
						msg.FileName = origin;
					}
				}

				messages.Add (msg);
			}
		}

		public void ParseMessages ()
		{
			messages.Clear ();
			ParseMessages (messages, output.ToString ().Split ('\n'), MessageToolName);
		}

		public bool HasErrorPattern (string prefix, int number, string messagePattern)
		{
			foreach (var msg in messages) {
				if (msg.IsError && msg.Prefix == prefix && msg.Number == number && Regex.IsMatch (msg.Message, messagePattern))
					return true;
			}
			return false;
		}

		public int ErrorCount {
			get {
				return messages.Count ((v) => v.IsError);
			}
		}

		public int WarningCount {
			get {
				return messages.Count ((v) => v.IsWarning);
			}
		}

		public bool HasError (string prefix, int number, string message)
		{
			foreach (var msg in messages) {
				if (msg.IsError && msg.Prefix == prefix && msg.Number == number && msg.Message == message)
					return true;
			}
			return false;
		}

		public void AssertWarningCount (int count, string message = "warnings")
		{
			if (count != WarningCount)
				Assert.Fail ($"{message}\nExpected: {count}\nBut was: {WarningCount}\nWarnings:\n\t{string.Join ("\n\t", this.Messages.Where ((v) => v.IsWarning).Select ((v) => v.ToString ()))}");
		}

		public void AssertErrorCount (int count, string message = "errors")
		{
			Assert.AreEqual (count, ErrorCount, message);
		}

		public void AssertErrorPattern (int number, string messagePattern, string filename = null, int? linenumber = null, bool custom_pattern_syntax = false)
		{
			AssertErrorPattern (MessagePrefix, number, messagePattern, filename, linenumber, custom_pattern_syntax);
		}

		public void AssertErrorPattern (string prefix, int number, string messagePattern, string filename = null, int? linenumber = null, bool custom_pattern_syntax = false)
		{
			if (!messages.Any ((msg) => msg.Prefix == prefix && msg.Number == number))
				Assert.Fail (string.Format ("The error '{0}{1:0000}' was not found in the output.", prefix, number));

			// Custom pattern syntax: escape parenthesis and brackets so that they're treated like normal characters.
			var processedPattern = custom_pattern_syntax ? messagePattern.Replace ("(", "[(]").Replace (")", "[)]").Replace ("[]", "[[][]]") + "$" : messagePattern;
			var matches = messages.Where ((msg) => Regex.IsMatch (msg.Message, processedPattern));
			if (!matches.Any ()) {
				var details = messages.Where ((msg) => msg.Prefix == prefix && msg.Number == number && !Regex.IsMatch (msg.Message, processedPattern)).Select ((msg) => string.Format ("\tThe message '{0}' did not match the pattern '{1}'.", msg.Message, messagePattern));
				Assert.Fail (string.Format ("The error '{0}{1:0000}: {2}' was not found in the output:\n{3}", prefix, number, messagePattern, string.Join ("\n", details.ToArray ())));
			}

			AssertFilename (prefix, number, messagePattern, matches, filename, linenumber);
		}

		public void AssertError (int number, string message, string filename = null, int? linenumber = null)
		{
			AssertError (MessagePrefix, number, message, filename, linenumber);
		}

		public void AssertError (string prefix, int number, string message, string filename = null, int? linenumber = null)
		{
			if (!messages.Any ((msg) => msg.Prefix == prefix && msg.Number == number))
				Assert.Fail (string.Format ("The error '{0}{1:0000}' was not found in the output.", prefix, number));

			var matches = messages.Where ((msg) => msg.Message == message);
			if (!matches.Any ()) {
				var details = messages.
				                      Where ((msg) => msg.Prefix == prefix && msg.Number == number && msg.Message != message).
				                      Select ((msg) => string.Format ("\tMessage #{2} did not match:\n\t\tactual:   '{0}'\n\t\texpected: '{1}'", msg.Message, message, messages.IndexOf (msg) + 1));
				Assert.Fail (string.Format ("The error '{0}{1:0000}: {2}' was not found in the output:\n{3}", prefix, number, message, string.Join ("\n", details.ToArray ())));
			}

			AssertFilename (prefix, number, message, matches, filename, linenumber);
		}

		void AssertFilename (string prefix, int number, string message, IEnumerable<ToolMessage> matches, string filename, int? linenumber)
		{
			if (filename != null) {
				var hasDirectory = filename.IndexOf (Path.DirectorySeparatorChar) > -1;
				if (!matches.Any ((v) => {
					if (hasDirectory) {
						// Check the entire path
						return filename == v.FileName;
					} else {
						// Don't compare the directory unless one was specified.
						return filename == Path.GetFileName (v.FileName);
					}
				})) {
					var details = matches.Select ((msg) => string.Format ("\tMessage #{2} did not contain expected filename:\n\t\tactual:   '{0}'\n\t\texpected: '{1}'", hasDirectory ? msg.FileName : Path.GetFileName (msg.FileName), filename, messages.IndexOf (msg) + 1));
					Assert.Fail (string.Format ($"The filename '{filename}' was not found in the output for the error {prefix}{number:X4}: {message}:\n{string.Join ("\n", details.ToArray ())}"));
				}
			}

			if (linenumber != null) {
				if (!matches.Any ((v) => linenumber.Value == v.LineNumber)) {
					var details = matches.Select ((msg) => string.Format ("\tMessage #{2} did not contain expected line number:\n\t\tactual:   '{0}'\n\t\texpected: '{1}'", msg.LineNumber, linenumber, messages.IndexOf (msg) + 1));
					Assert.Fail (string.Format ($"The linenumber '{linenumber.Value}' was not found in the output for the error {prefix}{number:X4}: {message}:\n{string.Join ("\n", details.ToArray ())}"));
				}
			}
		}

		public void AssertWarningPattern (int number, string messagePattern)
		{
			AssertWarningPattern (MessagePrefix, number, messagePattern);
		}

		public void AssertWarningPattern (string prefix, int number, string messagePattern)
		{
			if (!messages.Any ((msg) => msg.Prefix == prefix && msg.Number == number))
				Assert.Fail (string.Format ("The warning '{0}{1:0000}' was not found in the output.", prefix, number));

			if (messages.Any ((msg) => Regex.IsMatch (msg.Message, messagePattern)))
				return;

			var details = messages.Where ((msg) => msg.Prefix == prefix && msg.Number == number && !Regex.IsMatch (msg.Message, messagePattern)).Select ((msg) => string.Format ("\tThe message '{0}' did not match the pattern '{1}'.", msg.Message, messagePattern));
			Assert.Fail (string.Format ("The warning '{0}{1:0000}: {2}' was not found in the output:\n{3}", prefix, number, messagePattern, string.Join ("\n", details.ToArray ())));
		}

		public void AssertWarning (int number, string message, string filename = null, int? linenumber = null)
		{
			AssertWarning (MessagePrefix, number, message, filename, linenumber);
		}

		public void AssertWarning (string prefix, int number, string message, string filename = null, int? linenumber = null)
		{
			if (!messages.Any ((msg) => msg.Prefix == prefix && msg.Number == number))
				Assert.Fail (string.Format ("The warning '{0}{1:0000}' was not found in the output.", prefix, number));

			var matches = messages.Where ((msg) => msg.Message == message);
			if (!matches.Any ()) {
				var details = messages.Where ((msg) => msg.Prefix == prefix && msg.Number == number && msg.Message != message).Select ((msg) => string.Format ("\tMessage #{2} did not match:\n\t\tactual:   '{0}'\n\t\texpected: '{1}'", msg.Message, message, messages.IndexOf (msg) + 1));
				Assert.Fail (string.Format ("The warning '{0}{1:0000}: {2}' was not found in the output:\n{3}", prefix, number, message, string.Join ("\n", details.ToArray ())));
			}

			AssertFilename (prefix, number, message, matches, filename, linenumber);
		}

		public void AssertNoWarnings ()
		{
			var warnings = messages.Where ((v) => v.IsWarning);
			if (!warnings.Any ())
				return;

			Assert.Fail ("No warnings expected, but got:\n{0}\t", string.Join ("\n\t", warnings.Select ((v) => v.Message).ToArray ()));
		}

		public bool HasOutput (string line)
		{
			return OutputLines.Contains (line);
		}

		public bool HasOutputPattern (string linePattern)
		{
			foreach (var line in OutputLines) {
				if (Regex.IsMatch (line, linePattern, RegexOptions.CultureInvariant))
					return true;
			}

			return false;
		}

		public void AssertOutputPattern (string linePattern)
		{
			if (!HasOutputPattern (linePattern))
				Assert.Fail (string.Format ("The output does not contain the line '{0}'", linePattern));
		}

		public void ForAllOutputLines (Action<string> action)
		{
			foreach (var line in OutputLines)
				action (line);
		}

		protected abstract string ToolPath { get; }
		protected abstract string MessagePrefix { get; }
		protected virtual string MessageToolName { get { return null; } }
	}

	class XBuild
	{
		public static string ToolPath {
			get
			{
				return Configuration.XIBuildPath;
			}
		}

		public static void BuildXM (string project, string configuration = "Debug", string platform = "iPhoneSimulator", string verbosity = null, TimeSpan? timeout = null, string [] arguments = null)
		{
			Build (project,
				new Dictionary<string, string> {
					{ "MD_APPLE_SDK_ROOT", Path.GetDirectoryName (Path.GetDirectoryName (Configuration.xcode_root)) },
					{ "TargetFrameworkFallbackSearchPaths", Path.Combine (Configuration.TargetDirectoryXM, "Library", "Frameworks", "Mono.framework", "External", "xbuild-frameworks") },
					{ "MSBuildExtensionsPathFallbackPathsOverride", Path.Combine (Configuration.TargetDirectoryXM, "Library", "Frameworks", "Mono.framework", "External", "xbuild") },
					{ "XamarinMacFrameworkRoot", Path.Combine (Configuration.TargetDirectoryXM, "Library", "Frameworks", "Xamarin.Mac.framework", "Versions", "Current") },
					{ "XAMMAC_FRAMEWORK_PATH", Path.Combine (Configuration.TargetDirectoryXM, "Library", "Frameworks", "Xamarin.Mac.framework", "Versions", "Current") },
				}, configuration, platform, verbosity, timeout, arguments);
		}

		public static void BuildXI (string project, string configuration = "Debug", string platform = "iPhoneSimulator", string verbosity = null, TimeSpan? timeout = null, string [] arguments = null)
		{
			Build (project,
				new Dictionary<string, string> {
					{ "MD_APPLE_SDK_ROOT", Path.GetDirectoryName (Path.GetDirectoryName (Configuration.xcode_root)) },
					{ "TargetFrameworkFallbackSearchPaths", Path.Combine (Configuration.TargetDirectoryXI, "Library", "Frameworks", "Mono.framework", "External", "xbuild-frameworks") },
					{ "MSBuildExtensionsPathFallbackPathsOverride", Path.Combine (Configuration.TargetDirectoryXI, "Library", "Frameworks", "Mono.framework", "External", "xbuild") },
					{ "MD_MTOUCH_SDK_ROOT", Path.Combine (Configuration.TargetDirectoryXI, "Library", "Frameworks", "Xamarin.iOS.framework", "Versions", "Current") },
				}, configuration, platform, verbosity, timeout, arguments);
		}

		static void Build (string project, Dictionary<string, string> environmentVariables, string configuration = "Debug", string platform = "iPhoneSimulator", string verbosity = null, TimeSpan? timeout = null, string [] arguments = null)
		{
			ExecutionHelper.Execute (ToolPath,
				new string [] {
					"--",
					$"/p:Configuration={configuration}",
					$"/p:Platform={platform}",
					$"/verbosity:{(string.IsNullOrEmpty (verbosity) ? "normal" : verbosity)}",
					"/r:True", // restore nuget packages which are used in some test cases
					"/t:Clean,Build", // clean and then build, in case we left something behind in a shared dir
					project
				}.Union (arguments ?? new string [] { }).ToArray (),
				environmentVariables: environmentVariables,
				timeout: timeout);
		}
	}

	static class ExecutionHelper {
		static int Execute (string fileName, IList<string> arguments, StringBuilder stdout, StringBuilder stderr, TimeSpan? timeout = null)
		{
			var psi = new ProcessStartInfo (fileName, StringUtils.FormatArguments (arguments));
			return Execute (psi, (line) => {
				lock (stdout)
					stdout.AppendLine (line);
			}, (line) => {
				lock (stderr)
					stderr.AppendLine (line);
			}, timeout);
		}

		static int Execute (ProcessStartInfo psi, StringBuilder stdout, StringBuilder stderr, TimeSpan? timeout = null)
		{
			return Execute (psi, (line) => {
				lock (stdout)
					stdout.AppendLine (line);
			}, (line) => {
				lock (stderr)
					stderr.AppendLine (line);
			}, timeout);
		}

		public static int Execute (string fileName, IList<string> arguments)
		{
			return Execute (fileName, arguments, null, null, null, null);
		}

		public static int Execute (string fileName, IList<string> arguments, TimeSpan? timeout)
		{
			return Execute (fileName, arguments, null, null, null, timeout);
		}

		public static int Execute (string fileName, IList<string> arguments, Action<string> stdout_callback = null, Action<string> stderr_callback = null, TimeSpan? timeout = null)
		{
			return Execute (fileName, arguments, null, stdout_callback, stderr_callback, timeout);
		}

		public static int Execute (string fileName, IList<string> arguments, string working_directory = null, Action<string> stdout_callback = null, Action<string> stderr_callback = null, TimeSpan? timeout = null)
		{
			var psi = new ProcessStartInfo (fileName, StringUtils.FormatArguments (arguments));
			psi.WorkingDirectory = working_directory;
			return Execute (psi, stdout_callback, stderr_callback, timeout);
		}

		public static int Execute (string fileName, IList<string> arguments, out StringBuilder output)
		{
			return Execute (fileName, arguments, out output, null, null);
		}

		public static int Execute (string fileName, IList<string> arguments, out StringBuilder output, string working_directory, TimeSpan? timeout = null)
		{
			output = new StringBuilder ();
			var psi = new ProcessStartInfo (fileName, StringUtils.FormatArguments (arguments));
			psi.WorkingDirectory = working_directory;
			var capturedOutput = output;
			var callback = new Action<string> ((v) => {
				lock (psi)
					capturedOutput.AppendLine (v);
			});
			return Execute (psi, callback, callback, timeout);
		}

		public static int Execute (string fileName, IList<string> arguments, out bool timed_out, Dictionary<string, string> environment_variables = null, Action<string> stdout_callback = null, Action<string> stderr_callback = null, TimeSpan? timeout = null)
		{
			var psi = new ProcessStartInfo (fileName, StringUtils.FormatArguments (arguments));
			if (environment_variables != null) {
				foreach (var ev in environment_variables)
					psi.EnvironmentVariables [ev.Key] = ev.Value;
			}
			return Execute (psi, out timed_out, stdout_callback, stderr_callback, timeout);
		}

		public static int Execute (string fileName, IList<string> arguments, out bool timed_out, string working_directory = null, Dictionary<string, string> environment_variables = null, Action<string> stdout_callback = null, Action<string> stderr_callback = null, TimeSpan? timeout = null)
		{
			var psi = new ProcessStartInfo (fileName, StringUtils.FormatArguments (arguments));
			psi.WorkingDirectory = working_directory;
			if (environment_variables != null) {
				foreach (var ev in environment_variables)
					psi.EnvironmentVariables [ev.Key] = ev.Value;
			}
			return Execute (psi, out timed_out, stdout_callback, stderr_callback, timeout);
		}

		public static int Execute (ProcessStartInfo psi, Action<string> stdout_callback = null, Action<string> stderr_callback = null, TimeSpan? timeout = null)
		{
			return Execute (psi, out var _, stdout_callback, stderr_callback, timeout);
		}

		public static int Execute (ProcessStartInfo psi, out bool timed_out, Action<string> stdout_callback = null, Action<string> stderr_callback = null, TimeSpan? timeout = null)
		{
			var watch = new Stopwatch ();
			watch.Start ();

			if (stdout_callback == null)
				stdout_callback = Console.WriteLine;
			if (stderr_callback == null)
				stderr_callback = Console.Error.WriteLine;

			try {
				psi.UseShellExecute = false;
				psi.RedirectStandardError = true;
				psi.RedirectStandardOutput = true;
				if (!string.IsNullOrEmpty (psi.WorkingDirectory))
					Console.Write ($"cd {StringUtils.Quote (psi.WorkingDirectory)} && ");
				Console.WriteLine ("{0} {1}", psi.FileName, psi.Arguments);
				using (var p = new Process ()) {
					p.StartInfo = psi;
					// mtouch/mmp writes UTF8 data outside of the ASCII range, so we need to make sure
					// we read it in the same format. This also means we can't use the events to get
					// stdout/stderr, because mono's Process class parses those using Encoding.Default.
					p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
					p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
					p.Start ();

					var outReader = new Thread (() =>
						{
							string l;
							while ((l = p.StandardOutput.ReadLine ()) != null) {
								stdout_callback (l);
							}
						})
					{
						IsBackground = true,
					};
					outReader.Start ();

					var errReader = new Thread (() =>
						{
							string l;
							while ((l = p.StandardError.ReadLine ()) != null) {
								stderr_callback (l);
							}
						})
					{
						IsBackground = true,
					};
					errReader.Start ();

					if (timeout == null)
						timeout = TimeSpan.FromMinutes (5);
					if (!p.WaitForExit ((int) timeout.Value.TotalMilliseconds)) {
						timed_out = true;
						Console.WriteLine ("Command didn't finish in {0} minutes:", timeout.Value.TotalMinutes);
						Console.WriteLine ("{0} {1}", p.StartInfo.FileName, p.StartInfo.Arguments);
						Console.WriteLine ("Will now kill the process");
						kill (p.Id, 9);
						if (!p.WaitForExit (1000 /* killing should be fairly quick */)) {
							Console.WriteLine ("Kill failed to kill in 1 second !?");
							return 1;
						}
					} else {
						timed_out = false;
					}

					outReader.Join (TimeSpan.FromSeconds (1));
					errReader.Join (TimeSpan.FromSeconds (1));

					return p.ExitCode;
				}
			} finally {
				Console.WriteLine ("{0} Executed in {1}: {2} {3}", DateTime.Now, watch.Elapsed.ToString (), psi.FileName, psi.Arguments);
			}
		}

		public static int Execute (string fileName, IList<string> arguments, out string output, TimeSpan? timeout = null)
		{
			var sb = new StringBuilder ();
			var psi = new ProcessStartInfo ();
			psi.FileName = fileName;
			psi.Arguments = StringUtils.FormatArguments (arguments);
			var rv = Execute (psi, sb, sb, timeout);
			output = sb.ToString ();
			return rv;
		}

		// The arguments are automatically quoted.
		public static int Execute (string fileName, IList<string> arguments, Dictionary<string, string> environmentVariables, StringBuilder stdout, StringBuilder stderr, TimeSpan? timeout = null, string workingDirectory = null)
		{
			return Execute (fileName, StringUtils.FormatArguments (arguments), environmentVariables, stdout, stderr, timeout, workingDirectory);
		}

		static int Execute (string fileName, string arguments, Dictionary<string, string> environmentVariables, StringBuilder stdout, StringBuilder stderr, TimeSpan? timeout = null, string workingDirectory = null)
		{
			if (stdout == null)
				stdout = new StringBuilder ();
			if (stderr == null)
				stderr = new StringBuilder ();

			var psi = new ProcessStartInfo ();
			psi.FileName = fileName;
			psi.Arguments = arguments;
			if (!string.IsNullOrEmpty (workingDirectory))
				psi.WorkingDirectory = workingDirectory;
			if (environmentVariables != null) {
				var envs = psi.EnvironmentVariables;
				foreach (var kvp in environmentVariables) {
					envs [kvp.Key] = kvp.Value;
				}
			}

			return Execute (psi, stdout, stderr, timeout);
		}

		[DllImport ("libc")]
		private static extern void kill (int pid, int sig);
		public static string Execute (string fileName, IList<string> arguments, bool throwOnError = true, Dictionary<string, string> environmentVariables = null, bool hide_output = false, TimeSpan? timeout = null)
		{
			return Execute (fileName, StringUtils.FormatArguments (arguments), throwOnError, environmentVariables, hide_output, timeout);
		}

		static string Execute (string fileName, string arguments, bool throwOnError = true, Dictionary<string, string> environmentVariables = null,
			bool hide_output = false, TimeSpan? timeout = null
		)
		{
			StringBuilder output = new StringBuilder ();
			int exitCode = Execute (fileName, arguments, environmentVariables, output, output, timeout);
			var throw_exc = throwOnError && exitCode != 0;
			if (!hide_output || throw_exc) {
				Console.WriteLine ("{0} {1}", fileName, arguments);
				Console.WriteLine (output);
				Console.WriteLine ("Exit code: {0}", exitCode);
			}
			if (throw_exc)
				throw new TestExecutionException ($"Execution failed (exit code: {exitCode}) for '{fileName} {arguments}'");
			return output.ToString ();
		}
	}

	class TestExecutionException : Exception {
		public TestExecutionException (string output)
			: base (output)
		{
		}
	}
}
