$ErrorActionPreference = 'Stop'
$script = [System.IO.File]::ReadAllText((Join-Path (Get-Location) 'tmp/EAI-COST/followup.ps1'), [System.Text.Encoding]::UTF8)
. ([scriptblock]::Create($script.Substring(0, $script.IndexOf("`n`$file ="))))
$p = 'EasyLabel/src/EasyAI.Wpf/'
Replace-Once ($p+'ViewModels/AgentsSettingsViewModel.cs') '        AgentAuthService auth = new AgentAuthService(env);' '        AgentAuthService auth = new AgentAuthService(new AgentOfflineEnvironment(env, () => main.Settings.AgentOfflineOnly));'
Replace-Once ($p+'ViewModels/LabelStepViewModel.cs') 'new AgentModelService(new SystemAgentEnvironment(), System.IO.Path.Combine(AgentRunFolder.DefaultRoot(), "models"))' 'new AgentModelService(new AgentOfflineEnvironment(new SystemAgentEnvironment(), () => _host.Settings.AgentOfflineOnly), System.IO.Path.Combine(AgentRunFolder.DefaultRoot(), "models"))'
Replace-Once ($p+'ViewModels/LabelStepViewModel.cs') '        if (ticket != _modelLoad || tool != _host.AgentBackend)' '        if (_host.Settings.AgentOfflineOnly || ticket != _modelLoad || tool != _host.AgentBackend)'
