src = r'E:\Code\EasyVision_Unisen - NoService\EasyLabel\src\EasyAI.Wpf'

def edit(path, pairs):
    raw = open(path, 'rb').read()
    bom = raw.startswith(b'\xef\xbb\xbf')
    crlf = b'\r\n' in raw
    s = raw.decode('utf-8-sig').replace('\r\n', '\n')
    for old, new in pairs:
        assert old in s, (path, old)
        s = s.replace(old, new, 1)
    if crlf:
        s = s.replace('\n', '\r\n')
    open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))

review = '''                <!-- Review row: the suggestions of the open image (Reject all / Accept all are the same commands as the Label bar). Collapsed when there are none. -->
                <Grid Margin="0,0,0,6" AutomationProperties.AutomationId="AgentChatReview">
                    <Grid.Style>
                        <Style TargetType="Grid">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding SuggestionCount}" Value="0">
                                    <Setter Property="Visibility" Value="Collapsed" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </Grid.Style>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <StackPanel Grid.Column="0" Orientation="Horizontal" VerticalAlignment="Center">
                        <TextBlock Style="{StaticResource BarCaptionStyle}" Text="{x:Static res:Strings.AgentReviewCaption}" />
                        <TextBlock VerticalAlignment="Center" FontWeight="SemiBold" Text="{Binding SuggestionCount}"
                                   AutomationProperties.AutomationId="AgentChatSuggestionCount" />
                    </StackPanel>
                    <ui:Button Grid.Column="1" Margin="0,0,6,0" Appearance="Secondary"
                               Content="{x:Static res:Strings.RejectAll}"
                               Icon="{ui:SymbolIcon Dismiss24}"
                               Command="{Binding RejectAllCommand}"
                               ToolTip="{x:Static res:Strings.RejectAllTip}"
                               AutomationProperties.AutomationId="AgentChatRejectAll" />
                    <ui:Button Grid.Column="2" Appearance="Primary"
                               Content="{x:Static res:Strings.AcceptAll}"
                               Icon="{ui:SymbolIcon CheckmarkCircle24}"
                               Command="{Binding AcceptAllCommand}"
                               ToolTip="{x:Static res:Strings.AcceptAllTip}"
                               AutomationProperties.AutomationId="AgentChatAcceptAll" />
                </Grid>

'''

start_row = '''                <!-- First image of a batch: pick any image of the project; Images "All" / "Remaining" start from it (it becomes the open image). -->
                <StackPanel Margin="0,6,0,0" Orientation="Horizontal" AutomationProperties.AutomationId="AgentChatStartImage">
                    <TextBlock Style="{StaticResource BarCaptionStyle}" Text="{x:Static res:Strings.AgentStartImageCaption}" />
                    <ui:Button x:Name="StartImagePill" Height="28" MaxWidth="280" Padding="10,0,6,0" CornerRadius="14"
                               Appearance="Secondary"
                               Click="OnStartImageClick"
                               AutomationProperties.AutomationId="AgentChatStartImagePill"
                               AutomationProperties.Name="{x:Static res:Strings.AgentStartImageCaption}">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock x:Name="StartImageText" MaxWidth="230" VerticalAlignment="Center" FontSize="12" TextTrimming="CharacterEllipsis" />
                            <ui:SymbolIcon Margin="6,0,0,0" VerticalAlignment="Center" FontSize="12" Symbol="ChevronDown24" />
                        </StackPanel>
                    </ui:Button>
                    <Popup x:Name="StartImagePopup" PlacementTarget="{Binding ElementName=StartImagePill}" Placement="Top" StaysOpen="False" AllowsTransparency="True">
                        <Border Margin="8" Padding="4" CornerRadius="8" BorderThickness="1"
                                BorderBrush="{DynamicResource ControlStrokeColorDefaultBrush}"
                                Background="{DynamicResource ApplicationBackgroundBrush}">
                            <ListBox x:Name="StartImageList" Width="320" MaxHeight="300"
                                     DisplayMemberPath="DisplayName"
                                     SelectionChanged="OnStartImageSelected"
                                     AutomationProperties.AutomationId="AgentChatStartImageList" />
                        </Border>
                    </Popup>
                </StackPanel>

'''

edit(src + r'\Views\AgentChatWindow.xaml', [
    ('                <!-- Composer: one rounded card', review + '                <!-- Composer: one rounded card'),
    ('                <!-- How many agent runs a batch keeps going', start_row + '                <!-- How many agent runs a batch keeps going'),
])

code = '''    // The start-image picker lists every image of the project; picking one makes it the open image, where Images All / Remaining begin.
    private bool _startImageSync;

    private void RefreshStartImageText()
    {
        StartImageText.Text = _host.SelectedImage?.DisplayName ?? string.Empty;
    }

    private void OnStartImageClick(object sender, RoutedEventArgs e)
    {
        _startImageSync = true;
        try
        {
            StartImageList.ItemsSource = _host.Images;
            StartImageList.SelectedItem = _host.SelectedImage;
        }
        finally
        {
            _startImageSync = false;
        }

        StartImagePopup.IsOpen = true;
        if (StartImageList.SelectedItem != null)
        {
            StartImageList.ScrollIntoView(StartImageList.SelectedItem);
        }
    }

    private void OnStartImageSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_startImageSync || StartImageList.SelectedItem is not ViewModels.ImageItemViewModel item)
        {
            return;
        }

        StartImagePopup.IsOpen = false;
        _host.SelectedImage = item;
        RefreshStartImageText();
    }

'''

edit(src + r'\Views\AgentChatWindow.xaml.cs', [
    ('    // Files dropped on the composer', code + '    // Files dropped on the composer'),
    ('        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainViewModel.IsAdvancedMode))\n        {\n            ApplyUiMode();\n        }',
     '        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainViewModel.IsAdvancedMode))\n        {\n            ApplyUiMode();\n        }\n\n        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainViewModel.SelectedImage))\n        {\n            RefreshStartImageText();\n        }'),
    ('        ApplyUiMode();\n    }\n\n    /// <summary>The user closed', '        ApplyUiMode();\n        RefreshStartImageText();\n    }\n\n    /// <summary>The user closed'),
])

en = {'AgentReviewCaption': 'Suggestions', 'AgentStartImageCaption': 'Start at'}
vi = {'AgentReviewCaption': 'Đề xuất', 'AgentStartImageCaption': 'Bắt đầu từ'}

def resx(path, d):
    block = ''.join('  <data name="%s" xml:space="preserve"><value>%s</value></data>\n' % (k, v) for k, v in d.items())
    edit(path, [('  <data name="AgentOptions" xml:space="preserve">', block + '  <data name="AgentOptions" xml:space="preserve">')])

resx(src + r'\Resources\Strings.resx', en)
resx(src + r'\Resources\Strings.vi.resx', vi)
designer = ''.join('    public static string %s => Get(nameof(%s));\n' % (k, k) for k in en)
edit(src + r'\Resources\Strings.Designer.cs', [('    public static string SetAgentMaxImages => Get(nameof(SetAgentMaxImages));\n', '    public static string SetAgentMaxImages => Get(nameof(SetAgentMaxImages));\n' + designer)])
