param([Parameter(Mandatory=$true)][string]$GameRoot,[Parameter(Mandatory=$true)][string]$Message,[ValidateSet('success','error','rollback')][string]$Kind='success',[string]$PreviewPath,[string]$PreviewFontPath,[switch]$UseFallbackFont,[ValidateSet(96,144,192)][int]$PreviewDpi=96)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if(-not $UseFallbackFont -and -not ('PncFramework.Typography.NativeFontCache' -as [type])){Add-Type -Path @((Join-Path $PSScriptRoot 'NativeFontRebuilder.cs'),(Join-Path $PSScriptRoot 'NativeFontCache.cs'))}
$cache=Join-Path $GameRoot 'BepInEx/framework-ui/cache/PNC-Native-Alkhemikal-LOCAL-CACHE.ttf'
if($PreviewPath -and $PreviewFontPath){$cache=[IO.Path]::GetFullPath($PreviewFontPath)}elseif(-not $UseFallbackFont){
    $fontError=''
    if(-not [PncFramework.Typography.NativeFontCache]::TryEnsure($GameRoot,$cache,[ref]$fontError)){throw ('Native typography unavailable: '+$fontError)}
}
if($UseFallbackFont){$family=[Windows.Media.FontFamily]::new('Georgia')}else{
    $fontDirectory=[Uri]::new(([IO.Path]::GetDirectoryName($cache).TrimEnd('\')+'\'))
    $family=[Windows.Media.FontFamily]::new($fontDirectory,'./#PNC Native Alkhemikal Cache')
}
$glyphFace=$null;$matched=$false
foreach($face in $family.GetTypefaces()){if($face.TryGetGlyphTypeface([ref]$glyphFace) -and $glyphFace.CharacterToGlyphMap.ContainsKey(65)){$matched=$true;break}}
if(-not $matched){throw 'Native glyph cache could not be loaded by WPF'}
$asset=Join-Path $PSScriptRoot 'art/PNC_Updater_Notification.png'
$bitmap=[Windows.Media.Imaging.BitmapImage]::new()
$bitmap.BeginInit();$bitmap.CacheOption=[Windows.Media.Imaging.BitmapCacheOption]::OnLoad;$bitmap.UriSource=[Uri]::new([IO.Path]::GetFullPath($asset));$bitmap.EndInit();$bitmap.Freeze()
$cropped=[Windows.Media.Imaging.CroppedBitmap]::new($bitmap,[Windows.Int32Rect]::new(50,215,2470,531));$cropped.Freeze()
$width=[Math]::Min(1100,[Windows.SystemParameters]::WorkArea.Width*.88)
$height=$width*531/2470
$window=[Windows.Window]::new();$window.Title='PNC Framework Updater';$window.WindowStyle='None';$window.ResizeMode='NoResize';$window.AllowsTransparency=$true;$window.Background=[Windows.Media.Brushes]::Transparent
$window.Width=$width;$window.Height=$height;$window.WindowStartupLocation='CenterScreen';$window.ShowInTaskbar=$true
# Completion is a user-requested desktop notification after the game exits.
# Set topmost before showing it; activate once, without repeated focus stealing.
$window.ShowActivated=$true;$window.Topmost=$true
$window.Add_ContentRendered({$window.Activate() | Out-Null;$window.Focus() | Out-Null})

$canvas=[Windows.Controls.Canvas]::new();$canvas.Width=$width;$canvas.Height=$height;$window.Content=$canvas
[Windows.Media.RenderOptions]::SetBitmapScalingMode($canvas,[Windows.Media.BitmapScalingMode]::NearestNeighbor)
[Windows.Media.TextOptions]::SetTextRenderingMode($canvas,[Windows.Media.TextRenderingMode]::Aliased)
[Windows.Media.TextOptions]::SetTextFormattingMode($canvas,[Windows.Media.TextFormattingMode]::Display)
$image=[Windows.Controls.Image]::new();$image.Source=$cropped;$image.Width=$width;$image.Height=$height;$image.IsHitTestVisible=$false;$canvas.Children.Add($image) | Out-Null
$ink=[Windows.Media.SolidColorBrush]::new([Windows.Media.Color]::FromRgb(65,31,9))
$heading=[Windows.Controls.TextBlock]::new();$heading.FontFamily=$family;$heading.FontSize=26;$heading.Foreground=$ink;$heading.Width=$width*.72;$heading.TextAlignment='Center'
$heading.Text=switch($Kind){'error'{'UPDATE NEEDS ATTENTION'}'rollback'{'PREVIOUS VERSION RESTORED'}default{'UPDATE COMPLETE'}}
[Windows.Controls.Canvas]::SetLeft($heading,$width*.14);[Windows.Controls.Canvas]::SetTop($heading,$height*.13);$canvas.Children.Add($heading) | Out-Null
$body=[Windows.Controls.TextBlock]::new();$body.FontFamily=$family;$body.FontSize=20;$body.Foreground=$ink;$body.TextWrapping='Wrap';$body.TextAlignment='Center';$body.Width=$width*.70;$body.MaxHeight=$height*.32
$clean=$Message.Replace([string][char]0x2014,' - ').Replace([string][char]0x2019,"'")
if($Kind -eq 'error'){$clean='The update could not be completed. Your rollback backup was retained. Please check the updater log before restarting.'}
if($clean.Length -gt 210){$clean=$clean.Substring(0,207)+'...'}
$body.Text=$clean
[Windows.Controls.Canvas]::SetLeft($body,$width*.15);[Windows.Controls.Canvas]::SetTop($body,$height*.31);$canvas.Children.Add($body) | Out-Null
$buttons=[Windows.Controls.StackPanel]::new();$buttons.Orientation='Horizontal';$buttons.HorizontalAlignment='Center'
function New-ParchmentButton([string]$text){
    $button=[Windows.Controls.Button]::new();$button.Content=$text;$button.FontFamily=$family;$button.FontSize=19;$button.Foreground=$ink
    $button.Background=[Windows.Media.SolidColorBrush]::new([Windows.Media.Color]::FromRgb(220,163,69));$button.BorderBrush=$ink;$button.BorderThickness=[Windows.Thickness]::new(2)
    $button.Padding=[Windows.Thickness]::new(16,3,16,3);$button.Margin=[Windows.Thickness]::new(5,0,5,0);$button.MinHeight=29
    $button.Template=[Windows.Markup.XamlReader]::Parse('<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button"><Border x:Name="Frame" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Background" Value="#F6C26D"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="#FFF1BB"/></Trigger></ControlTemplate.Triggers></ControlTemplate>')
    return $button
}
$dismiss=New-ParchmentButton 'OK';$dismiss.IsDefault=$true;$dismiss.Add_Click({$window.Close()});$buttons.Children.Add($dismiss) | Out-Null
if($Kind -ne 'error'){
    $restart=New-ParchmentButton 'Restart Game'
    $restart.Add_Click({
        $exe=Join-Path $GameRoot 'Post Nut Calamity.exe'
        if(Test-Path -LiteralPath $exe){. (Join-Path $PSScriptRoot 'RestartLaunch.ps1');$start=New-PncRestartStartInfo -GameRoot $GameRoot;[Diagnostics.Process]::Start($start) | Out-Null;$window.Close()}
    });$buttons.Children.Add($restart) | Out-Null
}
$buttons.Measure([Windows.Size]::new($width,$height))
[Windows.Controls.Canvas]::SetLeft($buttons,($width-$buttons.DesiredSize.Width)*.5);[Windows.Controls.Canvas]::SetTop($buttons,$height*.56);$canvas.Children.Add($buttons) | Out-Null
$window.Add_KeyDown({if($_.Key -eq 'Escape'){$window.Close()}})
$canvas.Add_MouseLeftButtonDown({if($_.OriginalSource -eq $image){$window.DragMove()}})
if($PreviewPath){
    $canvas.Measure([Windows.Size]::new($width,$height));$canvas.Arrange([Windows.Rect]::new(0,0,$width,$height));$canvas.UpdateLayout()
    $render=[Windows.Media.Imaging.RenderTargetBitmap]::new([int]($width*$PreviewDpi/96),[int]($height*$PreviewDpi/96),$PreviewDpi,$PreviewDpi,[Windows.Media.PixelFormats]::Pbgra32);$render.Render($canvas)
    $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($render))
    $file=[IO.File]::Open([IO.Path]::GetFullPath($PreviewPath),[IO.FileMode]::Create);try{$encoder.Save($file)}finally{$file.Dispose()}
}else{$window.ShowDialog() | Out-Null}
