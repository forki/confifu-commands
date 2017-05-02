﻿using Confifu.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Confifu.Commands
{
    public interface ICommandRunner
    {
        CommandRunResult Run(string commandName);
    }

    public class CommandRunResult
    {
        public bool Succeed { get; private set; }
        public string ErrorLog { get; private set; }
        public string InfoLog { get; private set; }

        private CommandRunResult() { }

        public static CommandRunResult Ok(string info)
            => new CommandRunResult { Succeed = true, InfoLog = info, ErrorLog = "" };

        public static CommandRunResult Fail(string error, string info = "")
            => new CommandRunResult { Succeed = false, ErrorLog = error, InfoLog = info };
    }

    class CommandRunner : ICommandRunner
    {
        readonly ILookup<string, ICommand> commandsLookups;
        readonly IReadOnlyCollection<ICommand> commands;
        readonly IConfigVariables vars;

        public CommandRunner(ICommandRepository commandRepository, IConfigVariables vars)
        {
            this.vars = vars;
            this.commandsLookups = commandRepository.GetCommands()
                .ToLookup(x => x.Definition().Name, StringComparer.CurrentCultureIgnoreCase);

            this.commands = commandRepository.GetCommands();
        }

        public CommandRunResult Run(string commandName)
        {
            var command = this.commandsLookups[commandName].FirstOrDefault();

            if (command == null)
                CommandRunResult.Fail(
                    $"Command {commandName} not found. Available commands: [{string.Join(", ", this.commands.Select(x => x.Definition().Name))}]");

            var def = command.Definition();
            var parameters = def.Parameters;

            var taskSpecificVars = new ConfigVariablesBuilder()
                .Add(vars)
                .Add(vars.WithPrefix($"Commands:{def.Name}:"))
                .Build();
            var missedRequiredParameters = parameters.Where(x => x.Required && taskSpecificVars[x.Name] == null);

            if (missedRequiredParameters.Any())
            {
                return Failed((error, info) =>
                {
                    error.WriteLine($"Missing required parameters {string.Join(", ", missedRequiredParameters.Select(x => "<" + x.Name + ">"))}");
                    new CommandHelpPrinter(info).Print(command);
                });
            }
            
            var varsWithDefaultParameters = new ConfigVariablesBuilder()
                .Add(new CommandDefinitionConfigVars(def))
                .Add(taskSpecificVars)
                .Build();

            return Succeed((error, info) =>
            {
                command.Run(new CommandRunContext(varsWithDefaultParameters, info, error));
            });
        }

        CommandRunResult Failed(Action<StringWriter, StringWriter> action)
        {
            var errorWriter = new StringWriter();
            var infoWriter = new StringWriter();

            action(errorWriter, infoWriter);

            return CommandRunResult.Fail(errorWriter.ToString(), infoWriter.ToString());
        }

        CommandRunResult Succeed(Action<StringWriter, StringWriter> action)
        {
            var errorWriter = new StringWriter();
            var infoWriter = new StringWriter();

            try
            {
                action(errorWriter, infoWriter);
                return CommandRunResult.Ok(infoWriter.ToString());
            }
            catch(Exception ex)
            {
                infoWriter.WriteLine("Exception occurred:");
                infoWriter.WriteLine(ex);

                return CommandRunResult.Fail(errorWriter.ToString(), infoWriter.ToString());
            }
        }
    }

    class CommandDefinitionConfigVars : IConfigVariables
    {
        public string this[string key] => this.defaultParametersLookup[key].FirstOrDefault();

        readonly CommandDefinition commandDefinition;
        readonly ILookup<string, string> defaultParametersLookup;

        public CommandDefinitionConfigVars(CommandDefinition commandDefinition)
        {
            this.commandDefinition = commandDefinition;

            this.defaultParametersLookup = commandDefinition.Parameters.ToLookup(x => x.Name, x => x.DefaultValue);
        }
    }

    class CommandHelpPrinter
    {
        readonly StringWriter writer;

        public CommandHelpPrinter(StringWriter writer)
        {
            this.writer = writer;
        }

        public void Print(ICommand command)
        {
            var def = command.Definition();
            this.writer.WriteLine($"  {def.Name}:");
            this.writer.WriteLine();

            this.writer.WriteLine($"    {def.Help}");
            this.writer.WriteLine();

            this.writer.WriteLine("  Command Parameters:");
            this.writer.WriteLine();

            foreach (var parameter in def.Parameters)
            {
                this.writer.WriteLine($"    <{parameter.Name}>:");
                var requiredStr = parameter.Required ? "Required!" : "Optional";
                var defaultValueStr = string.IsNullOrEmpty(parameter.DefaultValue) ? "<empty>" : parameter.DefaultValue;

                this.writer.WriteLine($"      {parameter.Help}");
                this.writer.WriteLine($"      {requiredStr}, DefaultValue: {defaultValueStr}");
            }

            this.writer.WriteLine();
            this.writer.WriteLine();
        }
    }

    class HelpCommand : ICommand
    {
        readonly Func<ICommandRepository> commandRepositoryThunk;

        public HelpCommand(Func<ICommandRepository> commandRepositoryThunk)
        {
            this.commandRepositoryThunk = commandRepositoryThunk;
        }

        public CommandDefinition Definition()
            => new CommandDefinition("help", @"prints help info", new List<ParameterDefinition> {});

        public void Run(CommandRunContext context)
        {
            context.Info.WriteLine("Usage: amin <command> [parameters]");
            context.Info.WriteLine("Use: amin <command> --help to print command's help");

            context.Info.WriteLine("Available commands: ");
            context.Info.WriteLine();

            foreach (var command in commandRepositoryThunk().GetCommands())
            {
                new CommandHelpPrinter(context.Info).Print(command);
            }
        }
    }
}