# Session Perf Tracker - AI Agent Guide

Welcome, Agent. This file provides critical context for understanding and modifying this codebase.

## Project Essence
Session Perf Tracker is a Windows desktop application for performance monitoring and process control. It targets power users and gamers who need to identify and manage "noisy" or suspicious processes.

## Architecture: Clean Architecture
This project follows a layered approach to separate concerns:
1. **Domain**: Pure business logic, models, and abstractions. **ZERO dependencies** on other projects or Windows-specific APIs.
2. **Infrastructure**: Implementation of abstractions (WMI, WinAPI, Storage, Collectors). Depends on **Domain**.
3. **App (WPF)**: UI layer using MVVM. Depends on **Domain** and **Infrastructure**.

## Tech Stack
- **Language**: C# 12+ / .NET 8
- **UI**: WPF (XAML)
- **MVVM**: CommunityToolkit.Mvvm
- **Storage**: JSON and SQLite
- **Communication**: Inter-process monitoring via WMI and Windows Performance Counters.

## AI Instructions
- **Modifying Logic**: Always check the Domain abstractions before changing Infrastructure implementations.
- **UI Changes**: Follow MVVM strictly. Do not put logic in code-behind (.xaml.cs) unless it is strictly UI-related.
- **Localization**: Strings are managed in src/SessionPerfTracker.App/Localization/. Update both RU and EN files.
- **Testing**: Look for existing patterns in the project before adding new services.

Refer to layer-specific README.md files in src/ subdirectories for deeper technical details.
