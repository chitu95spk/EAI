using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using EasyLabel.Commands;
using EasyLabel.Commands.Discovery;
using EasyLabel.Commands.Results;

namespace EasyAI.Desktop.Commands
{
    public interface ICommandDispatcher
    {
        T Invoke<T>(Func<T> action);
        Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken);
    }

    public interface IViewModelCommandSource
    {
        ViewModelCommandTarget Resolve(string id, JsonElement args);
    }

    public sealed class ViewModelCommandTarget
    {
        public ViewModelCommandTarget(ICommand command, object parameter, Func<Task> executeAsync)
        {
            Command = command ?? throw new ArgumentNullException(nameof(command));
            Parameter = parameter;
            ExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        }
        public ICommand Command { get; private set; }
        public object Parameter { get; private set; }
        public Func<Task> ExecuteAsync { get; private set; }

        public static ViewModelCommandTarget FromCommand(ICommand command, object parameter)
        {
            return new ViewModelCommandTarget(command, parameter, () =>
            {
                if (command is IAsyncRelayCommand asyncCommand) return asyncCommand.ExecuteAsync(parameter);
                command.Execute(parameter);
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>Uses exactly the selected VM command and parameter for both availability and execution.</summary>
    public sealed class ViewModelCommandEndpoint : ICommandEndpoint
    {
        private readonly string _id;
        private readonly IViewModelCommandSource _source;
        private readonly ICommandDispatcher _dispatcher;

        public ViewModelCommandEndpoint(string id, IViewModelCommandSource source, ICommandDispatcher dispatcher)
        {
            _id = id;
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public bool CanExecute(ICommandContext context, JsonElement args)
        {
            return _dispatcher.Invoke(() =>
            {
                ViewModelCommandTarget target = _source.Resolve(_id, args);
                return target.Command.CanExecute(target.Parameter);
            });
        }

        public Task<CommandResult> ExecuteAsync(ICommandContext context, JsonElement args, CancellationToken cancellationToken)
        {
            return _dispatcher.InvokeAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ViewModelCommandTarget target = _source.Resolve(_id, args);
                if (!target.Command.CanExecute(target.Parameter))
                    return CommandResult.Failure("PRECONDITION_FAILED", "cmd.err.precondition");
                // Await the existing async relay command; do not fire-and-forget ICommand.Execute.
                await target.ExecuteAsync();
                return CommandResult.Success();
            }, cancellationToken);
        }
    }
}
