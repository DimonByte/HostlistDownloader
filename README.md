<div align="center">

# HostlistDownloader

A basic utility for Windows and Linux designed for users to download multiple host files from remote URLs, remove empty lines and comments, and consolidate them into a single combined blocklist/whitelist file. Perfect for services like Portmaster.

[![GitHub issues](https://img.shields.io/github/issues/DimonByte/HostlistDownloader?style=flat-badge&distro=false)](https://github.com/lloyd99901/HostlistDownloader/issues)
[![GitHub stars](https://img.shields.io/github/stars/DimonByte/HostlistDownloader)](https://github.com/lloyd99901/HostlistDownloader/stargazers)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](/LICENSE)

<br/>
<div align="left">

## Core Capabilities

HostlistDownloader streamlines hostlists by automatically fetching lists from remote sources that the user configures and merging them into one combined-blocklist/whitelist txt file.

| Functions | Description |
| :--- | :--- |
| **Remote Fetching** | Downloads raw host files directly from URLs defined in settings JSON file. |
| **Supports automation** | HostlistDownloader runs via the configured settings JSON file, once run there is no user interaction needed. |
| **Host File Update Check** | Checks for host file updates before downloading via eTags, ensuring that HostlistDownloader download files that have updates. |
| **Multi-Threaded Downloads** | Customizable multi-threaded download to get and process hostfiles quicker. |
| **Custom User Blocklists** | Combine your user defined blocklists with the ones that are downloaded. |
| **Formatting** | Force a specific format on all the host files downloaded. (e.g. hosts (0.0.0.0 google.com), iponly, domain, dnsmasq, wildcard) |
| **Filtering and Removal of Duplicates**   | Automatically strips out empty lines, comments (`#`, `;`), and duplicates during consolidation. |

# How to configure

Define your source URLs and user-defined domains in the INI files located within `hostfiles/`. This configuration system provides clear separation between blocklists, whitelists, and raw downloads.

1. Run HostlistDownloader. It will create a default `settings.json` for you.
2. Edit the `settings.json` with your desired URL paths of the host lists (separated by line).
3. Optionally adjust other settings such as format type, maximum download threads, or log expiry.
4. Ensure source domains are accessible (or use proxies configured in app settings).
5. Run HostlistDownloader again; it will download the host lists and create `HLDcombined-...txt` outputs automatically, with duplicates and comments removed.

### File Structure Overview

| Path / Filename | Functionality |
| :--- | :--- |
| [`settings.json`](#) | Contains all configuration including URLs to remote hostfile lists for blocking/whitelisting. |
| [`hostfiles/blocklist/HLDcombined-blocklist.txt`](#)     | **Output**: Consolidated list containing all blocklist URLs processed and merged locally. |
| [`hostfiles/whitelist/HLDcombined-whitelist.txt`](#)     | **Output**: Consolidated list containing all whitelist URLs processed and merged locally. |
| [`hostfiles/combined/HLDcombined-list.txt`](#)     | **Output**: Consolidated list containing all blocklist URLs, with blocklist entries that are in the whitelist removed. (Good for Portmasters "Custom Filter File" that doesn't have a whitelist filter file option) |

### Example `settings.json`

```json
{
  "blocklists": [
    "https://cdn.jsdelivr.net/gh/hagezi/dns-blocklists@latest/wildcard/ultimate-onlydomains.txt",
    "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts",
    "https://someonewhocares.org/hosts/zero/hosts",
  ],
  "whitelist": [],
  "formattype": "domain",
  "userWebsiteBlocklist": [],
  "userWebsiteWhitelist": [],
  "maxDownloadThreads": 3,
  "logExpiryInDays": 7
}
```

"formattype" Accepted Values: domain, host, iponly, dnsmasq (output: address=/hostnamehere/0.0.0.0), wildcard (output: *.hostnamehere)
