using System.Threading.Tasks;

namespace Rover.Core
{
    public interface IDebugCapability
    {
        string Name { get; }

        Task StartAsync(DebugHostContext context);
        Task StopAsync();

        void RegisterTools(IMcpToolRegistry registry);
    }
}
