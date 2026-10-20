namespace EasyLabel.Core.Commands
{
    public interface ICommand
    {
        string Name { get; }
        void Execute();
        void Undo();
    }
}
