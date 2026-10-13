# Contributing to MsiForge

Thank you for your interest in contributing to MsiForge! This document provides guidance on how to contribute to the project.

## Code of Conduct

Please be respectful and constructive in all interactions. We aim to create a welcoming environment for everyone.

## How to Contribute

### Reporting Issues

If you find a bug or have a feature request:

1. Check existing issues to see if it's already been reported
2. Provide detailed information about the issue
3. Include steps to reproduce for bugs
4. Include your environment (OS, .NET version, etc.)

### Contributing Code

1. Fork the repository
2. Create a new branch for your changes
3. Make your changes following the existing code style
4. Run tests to ensure everything works
5. Submit a pull request with a clear description

## Development Setup

### Prerequisites

- .NET 10.0 SDK or later
- Git

### Building

```bash
dotnet build MsiForge.slnx
```

### Running Tests

```bash
dotnet test tests/MsiForge.Tests/MsiForge.Tests.csproj
```

### Running the App

```bash
dotnet run --project src/MsiForge.App
```

## Code Style

- Follow the existing code style in the project
- Use meaningful variable and method names
- Add comments where appropriate
- Keep methods focused and single-purpose

## Testing

We take testing seriously:

- All tests must pass before PRs can be merged
- The selftest verifies the complete build cycle
- Tests run on Windows, macOS, and Linux

## License

By contributing, you agree that your contributions will be licensed under the MIT License. See LICENSE.txt for details.

## Questions?

Feel free to open an issue for any questions about contributing!
