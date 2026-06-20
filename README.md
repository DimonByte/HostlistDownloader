<div align="center">

# HostDownloader

A basic utility for Windows and Linux designed for users to download multiple host files from remote URLs, remove empty lines and comments, and consolidate them into a single combined blocklist/whitelist file. Perfect for services like Portmaster.

[![GitHub issues](https://img.shields.io/github/issues/DimonByte/HostDownloader?style=flat-badge&distro=false)](https://github.com/lloyd99901/HostDownloader/issues)
[![GitHub stars](https://img.shields.io/github/stars/DimonByte/hostdownloader?label=supporters)](https://github.com/lloyd99901/HostDownloader/stargazers)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](/LICENSE)

<br/>
<div align="left">

## Core Capabilities

HostDownloader streamlines hostlists by automatically fetching lists from remote sources that the user configures and merging them into one combined-blocklist/whitelist txt file.

| Functions | Description |
| :--- | :--- |
| **Remote Fetching** | Downloads raw host files directly from URLs defined in configuration INI settings. |
| **Filtering and Removal of Duplicates**   | Automatically strips empty lines, comments (`#`, `;`), and duplicates during consolidation. |

# How to configure

Define your source URLs and user-defined domains in the INI files located within `hostfiles/`. This configuration system provides clear separation between blocklists, whitelists, and raw downloads.

### File Structure Overview

| Path / Filename | Functionality |
| :--- | :--- |
| [`hostfiles/blocklist.ini`](#) | Contains URLs to remote hostfile lists for blocking (downloaded by line). |
| [`hostfiles/whitelist.ini`](#)  | Contains URLs to remote whitelists. |
| [`hostfiles/userwebsiteblocklist.ini`](#)| Individual domain-only blocks (e.g., `google.com` prevents website access). |
| [`hostfiles/userwebsitewhitelist.ini`](#)| Individual domain-only allows (e.g., `google.com` allows website access). |
| [`hostfiles/blocklist/combined-blocklist.txt`](#)     | **Output**: Consolidated list containing all blocklist URLs processed and merged locally. |
| [`hostfiles/whitelist/combined-whitelist.txt`](#)     | **Output**: Consolidated list containing all blocklist URLs processed and merged locally. |

> [!TIP] How to Configure
> 1. Edit the `blocklist.ini` files with URL paths of the host list. (Seperated by line)
> 2. Ensure source domains are accessible (or use proxies configured in app settings).
> 3. Run the utility; it will download the host lists and will create `combined-...txt` outputs automatically