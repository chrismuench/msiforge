# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| 1.x.x   | :white_check_mark: |

## Reporting a Vulnerability

We take security issues seriously. Thank you for improving the security of this open source project.

### How to Report

Please report security vulnerabilities **privately** by email to:

**security@[your-domain].com**

Do **NOT** use GitHub issues to report security vulnerabilities as they become public immediately.

### What to Include

When reporting a security vulnerability, please include:

1. Type of vulnerability (buffer overflow, SQL injection, XSS, etc.)
2. Full details on how to reproduce the issue
3. Potential impact
4. Any mitigating factors
5. Your name/affiliation (optional)

### Response Timeline

We aim to respond to security reports within 72 hours and will keep you informed of our progress.

### Security Updates

Security updates will be published on our GitHub repository with appropriate credit to the reporter.

## Security Best Practices

We follow secure development practices including:

- Static code analysis (CA2100, CA3xxx, CA5xxx)
- Input validation on all user-facing APIs
- Defense in depth for signing and verification
- Secure handling of certificates and credentials

## Known Security Features

- Cross-platform signing using osslsigncode
- Code signing verification
- Public key infrastructure support
- Safe handling of untrusted inputs

## Security Team

This is a community-driven project. Security matters are handled by the project maintainers.

## Security Disclaimer

This software is provided "as is" without warranty of any kind. Use at your own risk.
