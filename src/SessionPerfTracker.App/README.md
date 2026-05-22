# Session Perf Tracker - App Layer (WPF)

## Responsibility
The **Presentation** layer. Handles UI, User Interaction, and Visual Feedback.

## Patterns
- **MVVM**: Strict separation. 
  - Views/: XAML and minimal code-behind.
  - ViewModels/: Inherit from ObservableObject. Use IRelayCommand for actions.
- **Localization**: Managed via LocalizationManager. Resource dictionaries are in Localization/Strings.*.xaml.

## AI Instructions
- **Binding**: Use compiled bindings ({x:Bind}) or standard {Binding} with proper DataContext setup.
- **Async**: UI must remain responsive. Use Task.Run or sync/await for heavy operations.
- **Resources**: Global styles and icons are in Assets/.
- **Adding UI**: When adding a new feature, check if it needs a new ViewModel or can be integrated into MainWindowViewModel.
