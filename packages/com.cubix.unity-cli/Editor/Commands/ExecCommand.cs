using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Cubix.UnityCli
{
    public sealed class ExecContext
    {
        private readonly List<string> _logs = new List<string>();

        public IReadOnlyList<string> Logs => _logs;

        public void Log(string message)
        {
            _logs.Add(message);
            Debug.Log("[cubix-exec] " + message);
        }
    }

    [CubixCliCommand(Group = "exec", Name = "exec", Description = "Run one-off editor C# snippets.")]
    internal sealed class ExecCommand : ICubixCliCommandHandler, ICubixCliPreflightHandler
    {
        public IEnumerable<CommandDefinition> DescribeActions()
        {
            yield return new CommandDefinition
            {
                Action = "csharp",
                Description = "Compile and run a one-off C# snippet in the editor.",
                Tags = CommandMetadata.Tags(CommandTags.Experimental, CommandTags.Unsafe),
                SafetyLevel = CommandSafetyLevels.ArbitraryCode,
                SupportsPreflight = true,
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("code", "string", true, "C# snippet body to execute."),
                    CommandMetadata.Parameter("usings", "array", false, "Additional namespace imports."))
            };
        }

        public object Execute(string action, JObject parameters)
        {
            if (action != "csharp")
            {
                return new CommandErrorResponse("Unsupported exec action '" + action + "'.");
            }

            var code = parameters.Value<string>("code");
            if (string.IsNullOrWhiteSpace(code))
            {
                return new CommandErrorResponse("A C# code snippet is required.");
            }

            var provider = CodeDomProvider.CreateProvider("CSharp");
            if (provider == null)
            {
                return new CommandErrorResponse("C# CodeDOM provider is not available in this Unity environment.");
            }

            var compilerParameters = new CompilerParameters
            {
                GenerateExecutable = false,
                GenerateInMemory = true,
                TreatWarningsAsErrors = false
            };

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || string.IsNullOrWhiteSpace(assembly.Location))
                {
                    continue;
                }

                if (File.Exists(assembly.Location) && !compilerParameters.ReferencedAssemblies.Contains(assembly.Location))
                {
                    compilerParameters.ReferencedAssemblies.Add(assembly.Location);
                }
            }

            var source = BuildSource(code, parameters["usings"] as JArray);
            var results = provider.CompileAssemblyFromSource(compilerParameters, source);
            if (results.Errors.HasErrors)
            {
                var errors = new List<object>();
                foreach (CompilerError error in results.Errors)
                {
                    if (error.IsWarning)
                    {
                        continue;
                    }

                    errors.Add(new
                    {
                        line = error.Line,
                        column = error.Column,
                        message = error.ErrorText,
                        code = error.ErrorNumber
                    });
                }

                return new CommandErrorResponse("C# snippet compilation failed.", errors: errors);
            }

            var context = new ExecContext();
            try
            {
                var assembly = results.CompiledAssembly;
                var type = assembly.GetType("CubixCliDynamic.Executor");
                var method = type?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                var result = method?.Invoke(null, new object[] { context });
                return new CommandSuccessResponse("C# snippet executed.", new
                {
                    result = ValueSerializer.Serialize(result),
                    logs = context.Logs
                });
            }
            catch (TargetInvocationException exception)
            {
                return new CommandErrorResponse("C# snippet threw an exception.", errors: new[]
                {
                    new
                    {
                        type = exception.InnerException?.GetType().FullName ?? exception.GetType().FullName,
                        message = exception.InnerException?.Message ?? exception.Message,
                        stackTrace = exception.InnerException?.StackTrace ?? exception.StackTrace,
                        logs = context.Logs
                    }
                });
            }
        }

        public CommandPreflightResult Preflight(string action, JObject parameters)
        {
            if (action != "csharp")
            {
                return CommandMetadata.Result("exec." + action, CommandMetadata.Issue("error", "Unsupported exec action '" + action + "'.", "unsupported_action"));
            }

            var issues = new List<CommandPreflightIssue>();
            var code = parameters.Value<string>("code");
            if (string.IsNullOrWhiteSpace(code))
            {
                issues.Add(CommandMetadata.Issue("error", "A C# code snippet is required.", "missing_code"));
            }

            issues.Add(CommandMetadata.Issue("warning", "This command executes arbitrary editor code.", "arbitrary_code"));
            var result = CommandMetadata.Result("exec.csharp", issues.ToArray());
            if (issues.Count == 1)
            {
                result.summary = "Exec command can execute, but it runs arbitrary editor code.";
            }

            return result;
        }

        private static string BuildSource(string code, JArray usings)
        {
            var usingLines = new List<string>
            {
                "using System;",
                "using System.Linq;",
                "using UnityEditor;",
                "using UnityEngine;",
                "using Cubix.UnityCli;"
            };

            if (usings != null)
            {
                foreach (var token in usings.Values<string>())
                {
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        usingLines.Add("using " + token + ";");
                    }
                }
            }

            return string.Join(Environment.NewLine, usingLines.Distinct()) + Environment.NewLine +
                "namespace CubixCliDynamic" + Environment.NewLine +
                "{" + Environment.NewLine +
                "    public static class Executor" + Environment.NewLine +
                "    {" + Environment.NewLine +
                "        public static object Run(ExecContext ctx)" + Environment.NewLine +
                "        {" + Environment.NewLine +
                code + Environment.NewLine +
                "        }" + Environment.NewLine +
                "    }" + Environment.NewLine +
                "}";
        }
    }
}
