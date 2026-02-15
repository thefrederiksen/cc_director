# Visual Style Guide

> **Current framework scope:** WPF

Design reference for CC Director's dark-theme UI, inspired by VS Code. Use this guide when building new windows, dialogs, controls, and panels to maintain visual consistency.

---

## Table of Contents

1. [Color Palette](#1-color-palette)
2. [Typography](#2-typography)
3. [Layout](#3-layout)
4. [Buttons](#4-buttons)
5. [Text Inputs](#5-text-inputs)
6. [Lists & Trees](#6-lists--trees)
7. [Tabs](#7-tabs)
8. [Scrollbars](#8-scrollbars)
9. [Dialogs](#9-dialogs)
10. [Badges & Status Indicators](#10-badges--status-indicators)
11. [Icons & Shapes](#11-icons--shapes)
12. [Spacing & Margins](#12-spacing--margins)
13. [Interactive States](#13-interactive-states)
14. [App.xaml Resources](#14-appxaml-resources)

---

## 1. Color Palette

### Core Colors

| Token | Hex | Usage |
|-------|-----|-------|
| `PanelBackground` | `#1E1E1E` | Main content area, terminal, window background |
| `SidebarBackground` | `#252526` | Sidebar panels, dialog backgrounds, prompt bar |
| `ButtonBackground` | `#3C3C3C` | Secondary buttons, tab backgrounds, borders |
| `ButtonHover` | `#505050` | Button hover state |
| `TextForeground` | `#CCCCCC` | Primary text, labels, headings |
| `AccentBrush` | `#007ACC` | Primary action buttons, selected tab underline, OK buttons |
| `SelectedItemBrush` | `#094771` | Selected list item background |

### Semantic Colors

| Color | Hex | Usage |
|-------|-----|-------|
| Success green | `#22C55E` | Resume/confirm buttons, ahead badge |
| Blue badge | `#3B82F6` | Message count badges, behind-main badge |
| Warning amber | `#D97706` | Uncommitted count badge |
| Amber badge | `#F59E0B` | Behind-remote badge text |

### Text Hierarchy

| Level | Hex | Opacity | Usage |
|-------|-----|---------|-------|
| Primary | `#CCCCCC` | 1.0 | Headings, labels, body text |
| Secondary | `#AAAAAA` | 1.0 | Descriptions, summaries |
| Tertiary | `#888888` | 1.0 | Timestamps, hints, dimmed labels |
| Muted | `#666666` | 1.0 | Folder paths, session IDs |
| Placeholder | `#555555` | 1.0 | Empty states, build info, disabled text |

### Interactive Surface Colors

| Surface | Hex | Usage |
|---------|-----|-------|
| Hover overlay | `#2A2D2E` | Tree item hover, section toggle hover |
| Badge background | `#404040` | Count badges in section headers |
| Border | `#3C3C3C` | Input borders, panel separators, tab control borders |
| Status badge bg | `#336B7280` | Activity state badges (semi-transparent) |

### Badge Background Colors (Subtle)

| Badge | Background | Text | Usage |
|-------|-----------|------|-------|
| Ahead | `#1B3A2A` | `#22C55E` | Commits ahead of remote |
| Behind | `#3A2A1B` | `#F59E0B` | Commits behind remote |
| Behind main | `#1B2A3A` | `#3B82F6` | Commits behind main branch |

---

## 2. Typography

### Font Families

| Context | Font Stack |
|---------|-----------|
| Terminal / Code | `Cascadia Mono, Consolas, Courier New` |
| UI labels | System default (Segoe UI on Windows) |

### Font Sizes

| Size | Usage |
|------|-------|
| 18px | Session header name |
| 16px | Placeholder text, special buttons |
| 14px | Dialog body text, section labels, heading text |
| 13px | Prompt input, list item primary text |
| 12px | Panel headers ("SESSIONS", "SOURCE CONTROL"), tab headers, branch names, file names |
| 11px | Section toggle labels, metadata text, time ago text |
| 10px | Status text in list items, folder paths, badge text, search/filter descriptions |
| 9px | Build info, pipe message details, Claude info text, message count badge text, timestamps |

### Font Weights

| Weight | Usage |
|--------|-------|
| `SemiBold` | Panel headers, section titles, badge text, session header name |
| `Bold` | Three-dot menu button, status characters in git view |
| `Medium` | Session display name, list item primary text |
| Normal | Body text, descriptions, paths |
| `Italic` | Loading indicators ("Loading sessions...") |

---

## 3. Layout

### Main Window Structure

```
+------------------+---+---------------------------------+--+------------------+
| Sidebar (264px)  | 3 | Center Content (flex)           |24| Pipe Panel (0/350)|
|                  |px |                                 |px|                  |
| - Header         |   | - Session Header Banner         |  | - Header         |
| - Session List   |   | - Tab Control                   |  | - Message List   |
| - Action Buttons |   |   - Terminal + Scrollbar        |  |                  |
| - Build Info     |   |   - Source Control               |  |                  |
|                  |   |   - Repositories                |  |                  |
|                  |   | - Prompt Bar                    |  |                  |
+------------------+---+---------------------------------+--+------------------+
```

### Column Widths

| Column | Width | Notes |
|--------|-------|-------|
| Sidebar | `264` | `MinWidth="180"` |
| Left splitter | `Auto` (3px) | `GridSplitter` |
| Center content | `*` (fills remaining) | |
| Toggle strip | `Auto` (24px) | Pipe panel toggle |
| Pipe messages | `0` (collapsed) or `350` | Right panel |

### Window Defaults

| Property | Main Window | Dialog |
|----------|-------------|--------|
| Width | 1400 | 360-900 |
| Height | 700 | SizeToContent or 600 |
| MinWidth | 800 | varies |
| MinHeight | 500 | varies |
| Background | `PanelBackground` (#1E1E1E) | `SidebarBackground` (#252526) |
| StartupLocation | CenterScreen | CenterOwner |

---

## 4. Buttons

### Primary Action Button

The main action a user should take. Uses accent blue.

```xml
<Button Content="+ New Session"
        Height="30"
        Background="{StaticResource AccentBrush}"   <!-- #007ACC -->
        Foreground="White"
        BorderThickness="0"
        Cursor="Hand" />
```

### Secondary Button

Supporting actions. Uses button gray.

```xml
<Button Content="Open Logs"
        Height="30"
        Background="{StaticResource ButtonBackground}"   <!-- #3C3C3C -->
        Foreground="{StaticResource TextForeground}"      <!-- #CCCCCC -->
        BorderThickness="0"
        Cursor="Hand" />
```

### Confirm/Success Button

Positive confirmations (Resume, Create).

```xml
<Button Content="Resume Selected"
        Width="120"
        Background="#22C55E"
        Foreground="White"
        BorderThickness="0"
        Cursor="Hand" />
```

### Cancel Button

Always paired with a primary action.

```xml
<Button Content="Cancel"
        Width="80"
        Background="#3C3C3C"
        Foreground="#CCCCCC"
        BorderThickness="0"
        Cursor="Hand"
        IsCancel="True" />
```

### Toolbar Button (Inline)

Small buttons in toolbar rows.

```xml
<Button Content="Clone Repo"
        Height="28" Padding="12,0"
        Background="{StaticResource AccentBrush}"
        Foreground="White"
        BorderThickness="0"
        Cursor="Hand" />
```

### Ghost/Transparent Button

Borderless, blends into background. For menus, toggles.

```xml
<Button Content="..."
        Background="Transparent"
        Foreground="#888888"
        BorderThickness="0"
        Cursor="Hand"
        FontWeight="Bold" />
```

### Common Button Properties

- `BorderThickness="0"` on all buttons (borderless style)
- `Cursor="Hand"` on all clickable buttons
- `Focusable="False"` on toolbar buttons that shouldn't steal keyboard focus
- Standard heights: 28px (toolbar), 30px (sidebar/dialog)
- Standard widths: 60-80px (action), 120px (dialog primary)

---

## 5. Text Inputs

### Standard TextBox

```xml
<TextBox Background="#1E1E1E"
         Foreground="#CCCCCC"
         CaretBrush="#CCCCCC"
         BorderBrush="#3C3C3C"
         BorderThickness="1"
         Padding="8,6" />
```

### Code/Prompt Input

Multi-line input with monospace font:

```xml
<TextBox AcceptsReturn="True"
         TextWrapping="Wrap"
         MinLines="3"
         MinHeight="65"
         MaxHeight="160"
         VerticalScrollBarVisibility="Auto"
         Background="#1E1E1E"
         Foreground="#CCCCCC"
         CaretBrush="#CCCCCC"
         BorderBrush="#3C3C3C"
         BorderThickness="1"
         Padding="8,6"
         FontFamily="Cascadia Mono, Consolas, Courier New"
         FontSize="13" />
```

### Search/Filter Box

```xml
<TextBox Background="#1E1E1E"
         Foreground="#CCCCCC"
         BorderBrush="#3C3C3C"
         Padding="8"
         ToolTip="Filter by name or path" />
```

---

## 6. Lists & Trees

### ListBox

```xml
<ListBox Background="Transparent"
         BorderThickness="0"
         Foreground="{StaticResource TextForeground}"
         Margin="4,0" />
```

For bordered lists (inside dialogs):

```xml
<ListBox Background="#1E1E1E"
         Foreground="#CCCCCC"
         BorderBrush="#3C3C3C"
         BorderThickness="1" />
```

### ListBox Item Style (Session List)

Remove default padding, stretch content:

```xml
<ListBox.ItemContainerStyle>
    <Style TargetType="ListBoxItem">
        <Setter Property="IsTabStop" Value="False" />
        <Setter Property="Padding" Value="0" />
        <Setter Property="Margin" Value="0" />
        <Setter Property="HorizontalContentAlignment" Value="Stretch" />
    </Style>
</ListBox.ItemContainerStyle>
```

### Activity Border (Left Edge)

Colored left border indicates session state:

```xml
<Border BorderBrush="{Binding ActivityBrush}"
        BorderThickness="4,0,0,0"
        Padding="6,2,0,2">
    <!-- Content -->
</Border>
```

### TreeView

```xml
<TreeView Background="Transparent"
          BorderThickness="0"
          Padding="0" Margin="4,0,0,0" />
```

TreeViewItem hover:
```xml
<!-- Hover background on tree items -->
<Trigger Property="IsMouseOver" Value="True">
    <Setter Property="Background" Value="#2A2D2E" />
</Trigger>
```

### Virtualization for Large Lists

```xml
<ListBox VirtualizingPanel.IsVirtualizing="True"
         VirtualizingPanel.VirtualizationMode="Recycling"
         ScrollViewer.HorizontalScrollBarVisibility="Disabled" />
```

---

## 7. Tabs

### Tab Control

```xml
<TabControl Background="#1E1E1E" BorderThickness="0">
```

### Tab Item Style

Unselected: dark gray background, dimmed text.
Selected: panel background with accent underline.

```xml
<Style TargetType="TabItem">
    <Setter Property="Background" Value="#2D2D2D" />
    <Setter Property="Foreground" Value="#888888" />
    <Setter Property="Padding" Value="12,6" />
    <!-- Selected state -->
    <Trigger Property="IsSelected" Value="True">
        <Setter Property="Background" Value="#1E1E1E" />
        <Setter Property="Foreground" Value="#CCCCCC" />
        <!-- Blue accent underline (2px) via BottomAccent border -->
        <Setter TargetName="BottomAccent" Property="Background" Value="#007ACC" />
    </Trigger>
</Style>
```

### Dialog Tab Style (Simpler)

For tabs inside dialogs:

```xml
<TabItem Header="Resume Session"
         Background="#3C3C3C"
         Foreground="#CCCCCC" />
```

---

## 8. Scrollbars

### Terminal Scrollbar (Custom Styled)

Minimal design, no arrows, rounded thumb:

```xml
<!-- Track background matches panel -->
<Style x:Key="TerminalScrollBarStyle" TargetType="ScrollBar">
    <Setter Property="Background" Value="#1E1E1E" />
    <Setter Property="Width" Value="14" />
</Style>

<!-- Thumb: subtle gray, rounds on hover -->
<Style x:Key="TerminalScrollThumb" TargetType="Thumb">
    <Setter Property="Background" Value="#5A5A5A" />
    <!-- CornerRadius="2", Margin="3,0" -->
    <Trigger Property="IsMouseOver" Value="True">
        <Setter Property="Background" Value="#888888" />
    </Trigger>
    <Trigger Property="IsDragging" Value="True">
        <Setter Property="Background" Value="#AAAAAA" />
    </Trigger>
</Style>
```

### Standard ScrollViewer

```xml
<ScrollViewer VerticalScrollBarVisibility="Auto"
              HorizontalScrollBarVisibility="Disabled" />
```

---

## 9. Dialogs

### Dialog Template

All dialogs follow this pattern:

```xml
<Window Background="#252526"
        WindowStartupLocation="CenterOwner"
        ResizeMode="NoResize"          <!-- or CanResizeWithGrip -->
        SizeToContent="Height">        <!-- auto-size to content -->

    <Grid Margin="16">               <!-- or Margin="20" for simple dialogs -->
        <Grid.RowDefinitions>
            <RowDefinition Height="*" />     <!-- Content -->
            <RowDefinition Height="Auto" />  <!-- Buttons -->
        </Grid.RowDefinitions>

        <!-- Content area -->

        <!-- Button row, right-aligned -->
        <StackPanel Grid.Row="1" Orientation="Horizontal"
                    HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button Content="OK" Width="80"
                    Background="#007ACC" Foreground="White"
                    BorderThickness="0" Height="28" Cursor="Hand"
                    IsDefault="True" />
            <Button Content="Cancel" Width="80"
                    Background="#3C3C3C" Foreground="#CCCCCC"
                    BorderThickness="0" Height="28" Cursor="Hand"
                    IsCancel="True" Margin="8,0,0,0" />
        </StackPanel>
    </Grid>
</Window>
```

### Dialog Sizing

| Dialog Type | Width | Height | Resize |
|------------|-------|--------|--------|
| Simple confirmation | 360 | SizeToContent | NoResize |
| Text input | 400 | SizeToContent | NoResize |
| List selection | 900 | 600 | CanResizeWithGrip |
| Complex form | 500-700 | SizeToContent | NoResize |

### Dialog Margin Convention

- Simple dialogs: `Margin="20"` on outer Grid
- Complex dialogs: `Margin="16"` on outer Grid
- Content sections: `Margin="12"` within tabs

---

## 10. Badges & Status Indicators

### Count Badge (Pill)

```xml
<Border Background="#3B82F6"
        CornerRadius="8"
        Padding="5,1"
        Margin="6,0,0,0"
        VerticalAlignment="Center">
    <TextBlock Text="{Binding Count}"
               Foreground="White"
               FontSize="9"
               FontWeight="SemiBold" />
</Border>
```

### Section Count Badge

```xml
<Border CornerRadius="8"
        Background="#404040"
        Padding="6,1"
        Margin="8,0,0,0"
        VerticalAlignment="Center">
    <TextBlock Foreground="#CCCCCC"
               FontSize="10"
               FontWeight="SemiBold" />
</Border>
```

### State Badge (Rounded Rect)

```xml
<Border CornerRadius="4"
        Padding="10,4"
        Background="#336B7280">
    <TextBlock Text="Running"
               Foreground="White"
               FontSize="12"
               FontWeight="SemiBold" />
</Border>
```

### Git Ahead/Behind Badges

```xml
<!-- Ahead (green) -->
<Border CornerRadius="3" Background="#1B3A2A" Padding="4,1">
    <TextBlock Foreground="#22C55E" FontSize="10"
               FontFamily="Cascadia Mono, Consolas" />
</Border>

<!-- Behind (amber) -->
<Border CornerRadius="3" Background="#3A2A1B" Padding="4,1">
    <TextBlock Foreground="#F59E0B" FontSize="10"
               FontFamily="Cascadia Mono, Consolas" />
</Border>
```

### Conditional Visibility

Hide badges when data is unavailable:

```xml
<Border.Style>
    <Style TargetType="Border">
        <Setter Property="Visibility" Value="Visible" />
        <Style.Triggers>
            <DataTrigger Binding="{Binding HasData}" Value="False">
                <Setter Property="Visibility" Value="Collapsed" />
            </DataTrigger>
        </Style.Triggers>
    </Style>
</Border.Style>
```

---

## 11. Icons & Shapes

### Tree Expand Arrow

```xml
<Path Data="M 0,0 L 5,4 L 0,8 Z"
      Fill="#888888"
      HorizontalAlignment="Center"
      VerticalAlignment="Center"
      RenderTransformOrigin="0.5,0.5">
    <Path.RenderTransform>
        <RotateTransform Angle="0" />   <!-- 90 when expanded -->
    </Path.RenderTransform>
</Path>
```

### Branch Icon (Git)

SVG-style Path data for a branch icon:

```xml
<Path Data="M7,3 C7,1.9 7.9,1 9,1 ... Z"
      Fill="#888888"
      Width="14" Height="14"
      Stretch="Uniform" />
```

### Icon Colors

| State | Color |
|-------|-------|
| Default | `#888888` |
| Expanded/Active | `#CCCCCC` |
| Hover | `#CCCCCC` |

---

## 12. Spacing & Margins

### Standard Margins

| Context | Margin |
|---------|--------|
| Panel header | `12,12,12,8` |
| Sidebar buttons | `8,4,8,8` (outer), `0,2` (per button) |
| Dialog outer | `16` or `20` |
| Content within tabs | `12` |
| Button row | `0,12,0,0` (top spacing) |
| Button gap | `8,0,0,0` (between buttons) |
| Section spacing | `0,4,0,0` |
| Badge spacing | `6,0,0,0` or `8,0,0,0` |

### Standard Padding

| Element | Padding |
|---------|---------|
| TextBox | `8,6` or `8` |
| Buttons | `12,0` (toolbar) |
| Tab items | `12,6` |
| Badges | `5,1` (pill), `10,4` (state), `4,1` (git) |
| Session header | `16,8` |
| Tree items | `2,1` |

### Grid Splitter

```xml
<GridSplitter Width="3"
              Focusable="False"
              Background="{StaticResource PanelBackground}"
              HorizontalAlignment="Center"
              VerticalAlignment="Stretch" />
```

---

## 13. Interactive States

### Button States

| State | Background | Foreground |
|-------|-----------|------------|
| Default | `#3C3C3C` | `#CCCCCC` |
| Hover | `#505050` | `#CCCCCC` |
| Primary default | `#007ACC` | White |
| Ghost default | Transparent | `#888888` |
| Ghost hover | Transparent | `#CCCCCC` (Opacity 1.0) |
| Disabled | - | (IsEnabled=False) |

### Three-Dot Menu Button

```xml
<Button.Style>
    <Style TargetType="Button">
        <Setter Property="Opacity" Value="0.5" />
        <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Opacity" Value="1" />
                <Setter Property="Foreground" Value="#CCCCCC" />
            </Trigger>
        </Style.Triggers>
    </Style>
</Button.Style>
```

### Scrollbar Thumb States

| State | Background |
|-------|-----------|
| Default | `#5A5A5A` |
| Hover | `#888888` |
| Dragging | `#AAAAAA` |

### List Item States

| State | Effect |
|-------|--------|
| Hover | Background `#2A2D2E` |
| Selected | Background via `SelectedItemBrush` (`#094771`) |

---

## 14. App.xaml Resources

All shared resources must be defined in `App.xaml`:

```xml
<Application.Resources>
    <!-- Core brushes -->
    <SolidColorBrush x:Key="PanelBackground" Color="#1E1E1E" />
    <SolidColorBrush x:Key="SidebarBackground" Color="#252526" />
    <SolidColorBrush x:Key="ButtonBackground" Color="#3C3C3C" />
    <SolidColorBrush x:Key="ButtonHover" Color="#505050" />
    <SolidColorBrush x:Key="TextForeground" Color="#CCCCCC" />
    <SolidColorBrush x:Key="AccentBrush" Color="#007ACC" />
    <SolidColorBrush x:Key="SelectedItemBrush" Color="#094771" />

    <!-- Scrollbar styles -->
    <Style x:Key="ScrollBarPageButton" TargetType="RepeatButton">...</Style>
    <Style x:Key="TerminalScrollThumb" TargetType="Thumb">...</Style>
    <Style x:Key="TerminalScrollBarStyle" TargetType="ScrollBar">...</Style>
</Application.Resources>
```

### When to Add New Resources

Add a new resource to App.xaml when:
- A color or style is used in 3+ places
- A style defines a reusable component pattern
- A brush needs to be referenced by `{StaticResource}` key

Keep inline styles for one-off customizations within a single control.
