src = r'E:\Code\EasyVision_Unisen - NoService\EasyLabel\src\EasyAI.Wpf'
p = src + r'\Views\AgentChatWindow.xaml'
raw = open(p, 'rb').read()
bom = raw.startswith(b'\xef\xbb\xbf')
crlf = b'\r\n' in raw
s = raw.decode('utf-8-sig').replace('\r\n', '\n')

a = s.index('                <!-- Prompt: Enter runs, Shift+Enter is a new line. -->')
b = s.index('                <!-- Which images a run covers. -->')

card = '''                <!-- Composer: one rounded card with the attached files, the prompt (Enter runs, Shift+Enter is a new line) and one tool row. -->
                <Border x:Name="Composer" Margin="0,0,0,8" Padding="10,8,10,6" CornerRadius="12" BorderThickness="1"
                        BorderBrush="{DynamicResource ControlStrokeColorDefaultBrush}"
                        Background="{DynamicResource ControlFillColorDefaultBrush}"
                        AllowDrop="True" DragOver="OnComposerDragOver" Drop="OnComposerDrop"
                        AutomationProperties.AutomationId="AgentChatComposer">
                    <StackPanel>
                        <!-- Attached hint files (pictures of the defect, notes): the agent reads them with get_reference. Collapsed when empty. -->
                        <ItemsControl Margin="0,0,0,6"
                                      ItemsSource="{Binding Attachments}"
                                      Visibility="{Binding HasAttachments, Converter={StaticResource BoolToVisibility}}"
                                      AutomationProperties.AutomationId="AgentChatAttachments">
                            <ItemsControl.ItemsPanel>
                                <ItemsPanelTemplate>
                                    <WrapPanel />
                                </ItemsPanelTemplate>
                            </ItemsControl.ItemsPanel>
                            <ItemsControl.ItemTemplate>
                                <DataTemplate>
                                    <Border Margin="0,0,6,4" Padding="8,2,2,2" CornerRadius="10"
                                            Background="{DynamicResource ControlFillColorSecondaryBrush}"
                                            AutomationProperties.AutomationId="AgentChatAttachment">
                                        <StackPanel Orientation="Horizontal">
                                            <ui:SymbolIcon Margin="0,0,6,0" VerticalAlignment="Center" FontSize="14">
                                                <ui:SymbolIcon.Style>
                                                    <Style TargetType="ui:SymbolIcon">
                                                        <Setter Property="Symbol" Value="Document24" />
                                                        <Style.Triggers>
                                                            <DataTrigger Binding="{Binding IsImage}" Value="True">
                                                                <Setter Property="Symbol" Value="Image24" />
                                                            </DataTrigger>
                                                        </Style.Triggers>
                                                    </Style>
                                                </ui:SymbolIcon.Style>
                                            </ui:SymbolIcon>
                                            <TextBlock MaxWidth="150" VerticalAlignment="Center" FontSize="12" TextTrimming="CharacterEllipsis"
                                                       Text="{Binding Name}" ToolTip="{Binding Path}" />
                                            <ui:Button Width="22" Height="22" Margin="4,0,0,0" Padding="0" Appearance="Transparent"
                                                       Icon="{ui:SymbolIcon Dismiss12}"
                                                       Command="{Binding DataContext.RemoveAttachmentCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                                       CommandParameter="{Binding}"
                                                       AutomationProperties.AutomationId="AgentChatAttachmentRemove"
                                                       AutomationProperties.Name="{x:Static res:Strings.AgentRemoveAttachment}" />
                                        </StackPanel>
                                    </Border>
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>

                        <ui:TextBox x:Name="PromptBox" Margin="0,0,0,6" Padding="2"
                                    BorderThickness="0" Background="Transparent"
                                    MinLines="2" MaxLines="5" AcceptsReturn="True" TextWrapping="Wrap"
                                    VerticalScrollBarVisibility="Auto"
                                    PlaceholderText="{x:Static res:Strings.AgentPromptPlaceholder}"
                                    Text="{Binding AgentPrompt, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                                    AutomationProperties.AutomationId="AgentChatPrompt"
                                    AutomationProperties.Name="{x:Static res:Strings.AgentPromptPlaceholder}" />

                        <!-- One tool row: attach, suggest prompt, model + effort pill (one menu), options, then Run (Cancel while running) on the right. -->
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <ui:Button Grid.Column="0" Width="32" Height="32" Margin="0,0,4,0" Padding="0"
                                       Appearance="Transparent"
                                       Icon="{ui:SymbolIcon Add24}"
                                       Command="{Binding AttachFilesCommand}"
                                       ToolTip="{x:Static res:Strings.AgentAttach}"
                                       AutomationProperties.AutomationId="AgentChatAttach"
                                       AutomationProperties.Name="{x:Static res:Strings.AgentAttach}" />
                            <ui:Button Grid.Column="1" Width="32" Height="32" Margin="0,0,6,0" Padding="0"
                                       Appearance="Transparent"
                                       Icon="{ui:SymbolIcon Lightbulb24}"
                                       Command="{Binding SuggestPromptCommand}"
                                       ToolTipService.ShowOnDisabled="True"
                                       AutomationProperties.AutomationId="AgentSuggestPromptButton"
                                       AutomationProperties.Name="{x:Static res:Strings.SuggestPrompt}" />
                            <ui:Button x:Name="ModelPill" Grid.Column="2" Height="28" MinWidth="110" MaxWidth="260" Margin="0,0,6,0"
                                       HorizontalAlignment="Left" VerticalAlignment="Center"
                                       Padding="10,0,6,0"
                                       Appearance="Secondary"
                                       CornerRadius="14"
                                       Click="OnModelPillClick"
                                       AutomationProperties.AutomationId="PromptModel"
                                       AutomationProperties.Name="{x:Static res:Strings.PromptModel}">
                                <StackPanel Orientation="Horizontal">
                                    <TextBlock VerticalAlignment="Center" FontSize="12" TextTrimming="CharacterEllipsis"
                                               Text="{Binding AgentPillText, Mode=OneWay}" />
                                    <ui:SymbolIcon Margin="6,0,0,0" VerticalAlignment="Center" FontSize="12" Symbol="ChevronDown24" />
                                </StackPanel>
                            </ui:Button>
                            <ToggleButton x:Name="OptionsToggle" Grid.Column="3" Width="32" Height="32" Margin="0,0,6,0" Padding="0"
                                          Style="{DynamicResource {x:Type ToggleButton}}"
                                          AutomationProperties.AutomationId="AgentChatOptions"
                                          AutomationProperties.Name="{x:Static res:Strings.AgentOptions}">
                                <ui:SymbolIcon Symbol="Settings24" />
                            </ToggleButton>
                            <Grid Grid.Column="4">
                                <Grid Style="{StaticResource ShowWhenIdleStyle}">
                                    <ui:Button Width="32" Height="32" Padding="0" CornerRadius="16" Appearance="Primary"
                                               Icon="{ui:SymbolIcon ArrowUp24}"
                                               Command="{Binding RunChatCommand}"
                                               ToolTip="{x:Static res:Strings.TbRun}"
                                               AutomationProperties.AutomationId="AgentChatRun"
                                               AutomationProperties.Name="{x:Static res:Strings.TbRun}" />
                                </Grid>
                                <Grid Style="{StaticResource ShowWhenRunningStyle}">
                                    <ui:Button Width="32" Height="32" Padding="0" CornerRadius="16" Appearance="Secondary"
                                               Icon="{ui:SymbolIcon Stop24}"
                                               Command="{Binding CancelRunCommand}"
                                               ToolTip="{x:Static res:Strings.Cancel}"
                                               AutomationProperties.AutomationId="AgentChatCancel"
                                               AutomationProperties.Name="{x:Static res:Strings.Cancel}" />
                                </Grid>
                            </Grid>
                        </Grid>
                    </StackPanel>
                </Border>

'''
s = s[:a] + card + s[b:]

# Parallel runs row (always visible, under the image mode row).
marker = '                <!-- Options (collapsed until the gear is toggled)'
workers = '''                <!-- How many agent runs a batch keeps going at the same time (1 = one after the other; 3 is about 3x faster at Economy effort). -->
                <StackPanel Margin="0,6,0,0" Orientation="Horizontal" AutomationProperties.AutomationId="AgentChatWorkers">
                    <TextBlock Style="{StaticResource BarCaptionStyle}" Text="{x:Static res:Strings.AgentWorkersCaption}" />
                    <RadioButton Margin="0,0,3,0" Padding="8,2" MinWidth="40" HorizontalContentAlignment="Center" GroupName="AgentChatWorkers"
                                 Style="{DynamicResource {x:Type ToggleButton}}"
                                 IsChecked="{Binding IsWorkers1, Mode=TwoWay}"
                                 AutomationProperties.AutomationId="AgentChatWorkers_1"
                                 Content="1" />
                    <RadioButton Margin="0,0,3,0" Padding="8,2" MinWidth="40" HorizontalContentAlignment="Center" GroupName="AgentChatWorkers"
                                 Style="{DynamicResource {x:Type ToggleButton}}"
                                 IsChecked="{Binding IsWorkers2, Mode=TwoWay}"
                                 AutomationProperties.AutomationId="AgentChatWorkers_2"
                                 Content="2" />
                    <RadioButton Padding="8,2" MinWidth="40" HorizontalContentAlignment="Center" GroupName="AgentChatWorkers"
                                 Style="{DynamicResource {x:Type ToggleButton}}"
                                 IsChecked="{Binding IsWorkers3, Mode=TwoWay}"
                                 AutomationProperties.AutomationId="AgentChatWorkers_3"
                                 Content="3" />
                </StackPanel>

'''
assert marker in s
s = s.replace(marker, workers + marker, 1)

if crlf:
    s = s.replace('\n', '\r\n')
open(p, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))

# code-behind drag/drop
q = src + r'\Views\AgentChatWindow.xaml.cs'
raw = open(q, 'rb').read()
bom = raw.startswith(b'\xef\xbb\xbf')
crlf = b'\r\n' in raw
t = raw.decode('utf-8-sig').replace('\r\n', '\n')
anchor = '    // The pill opens one menu:'
assert anchor in t
handlers = '''    // Files dropped on the composer are attached as hints (pictures and notes; other kinds are skipped by AddAttachments).
    private void OnComposerDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnComposerDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            _host.Label.AddAttachments(files);
        }

        e.Handled = true;
    }

'''
t = t.replace(anchor, handlers + anchor, 1)
if crlf:
    t = t.replace('\n', '\r\n')
open(q, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + t.encode('utf-8'))
