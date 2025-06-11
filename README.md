# GitHub Copilot Auto-Assign & Deprovision Tool

## Overview
This codebase is a proof-of-concept (POC) tool designed to automate the deprovisioning of GitHub Copilot seats for standalone customers using Azure Entra (formerly Azure Active Directory) to manage license assignments. The tool monitors user activity and manages Copilot licenses by sending warning emails to inactive users and removing them from the license group if inactivity persists.

## Prerequisites
- **Azure App Registration:**
  - Register an application in Azure Entra (Azure AD).
  - Grant the following Microsoft Graph API permissions:
    - `User.Read.All`
    - `GroupMember.ReadWrite.All`
    - `Group.Read.All`
    - `Mail.Send` (if using Graph API for email)
  - Create a client secret for the app registration.
  - Note the `clientId`, `tenantId`, and `clientSecret` for use in environment variables.
- **GitHub Personal Access Tokens (Classic):**
  - Create a personal access token (classic) with either "manage_billing:copilot" or "read:enterprise" scopes. 
- **SendGrid Account (optional):**
  - If using SendGrid for email, create an account and generate an API key.
- **Azure Subscription:**
  - An Azure subscription to deploy the Azure Function App.
- **.NET 9 SDK:**
  - Required for local development and build.
- **Azure Functions Core Tools:**
  - For local development and testing.

## How It Works
- **User Activity Monitoring:**
  - The tool checks GitHub Copilot usage for all users in a specified organization via the GitHub Copilot Metrics API.
  - If a user has not used Copilot for **30 days**, they receive a warning email.
  - If inactivity continues for **45 days**, the user receives a final email and is automatically removed from the Azure Entra group that grants the Copilot license.
- **License Assignment:**
  - Users in the specific Azure Entra group are automatically assigned a Copilot license. Removal from the group revokes the license, and this occurs once 45 days of inactivity is reached.
  - The function will then check each user's Entra properties within the SCIM GitHub Enterpise group, checking if the fields userPrincipalName starts with the GitHub Username, mailNickname starts with the GitHub Username, or employeeId starts with the GitHub External Id
- **Email Notifications:**
  - By default, emails are sent using [SendGrid](https://sendgrid.com/).
  - Optionally, you can use the Microsoft Graph API to send emails if your tenant has the required M365/Exchange Online licenses (see sample code in the repo).

## Project Hierarchy
```
├── ghcpfunc.cs           # Main Azure Function logic
├── ghcpfunc.csproj       # Project file
├── Program.cs            # Function host setup
├── Models/               # Data models
├── local.settings.json   # Local development settings
├── test.http             # Sample HTTP requests
└── ...                   # Other supporting files
```

## Required Environment Variables
Store these in a `.env` file for local development, or in the Azure Function App settings:
- `GITHUB_API_KEY`      : GitHub API token (classic) with manage_billing:copilot or read:enterprise scopes/permissions. To create this, go to GitHub -> Settings -> Developer Settings -> Personal access tokens -> Tokens (classic) -> Generate new token (classic) 
- `clientId`            : Azure app registration client ID
- `tenantId`            : Azure tenant ID
- `clientSecret`        : Azure app registration client secret
- `groupId`             : Azure Entra group ID for Copilot license assignment
- `sendGridAPIKey`      : SendGrid API key (if using SendGrid for email)
- `emailSender`         : Email address to send warnings from

## How to Run (Azure Function)
1. **Deploy as an Azure Function App** (recommended):
   - Set all required environment variables in the Function App configuration.
   - Deploy the code using Visual Studio, VS Code, or Azure CLI.
   - The function exposes an HTTP endpoint (see `test.http` for sample requests).
2. **Local Development:**
   - Place your `.env` file in the project root with all required variables.
   - Run the function locally using the Azure Functions Core Tools or Visual Studio.

## Email Sending Options
- **SendGrid (default):**
  - Fast and easy to set up. Requires a SendGrid account and API key.
- **Microsoft Graph API:**
  - Requires M365/Exchange Online/SharePoint Online license for the sender.
  - Sample code is provided in the repo (commented out in `ghcpfunc.cs`).

## Important Notes
- **POC Only:** This code is for proof-of-concept purposes and is **not fully tested**. Do **not** deploy to production environments.
- **Security:** Ensure all secrets and API keys are stored securely (use Azure Key Vault or Function App settings in production).
- **Extensibility:** The code can be extended to support other IdPs or email providers as needed.

---

For questions or contributions, please open an issue or pull request.
