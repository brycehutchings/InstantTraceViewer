# Instant Trace Viewer

[![Windows Build](https://github.com/brycehutchings/InstantTraceViewer/actions/workflows/build-windows.yml/badge.svg)](https://github.com/brycehutchings/InstantTraceViewer/actions/workflows/build-windows.yml)

Instant Trace Viewer is a developer tool for collecting and analyzing traces and logs.
It features a straightforward interface for viewing both real-time traces and trace files,
with filtering and graphical visualizations to help you quickly understand your program's behavior during development.

![image](https://github.com/user-attachments/assets/a996841e-afad-496b-85f7-6b23bb5b5e22)

Real-time trace sources supported:
* Event Tracing for Windows (ETW)
* Android Logcat

Supported file formats:
* ETL (Event Tracing for Windows)
* Perfetto
* CSV and TSV

## Installation

You can install **Instant Trace Viewer** from the Microsoft Store or using `winget` from the command line.

### Microsoft Store

[![Install from Microsoft Store](https://get.microsoft.com/images/en-us%20dark.svg)](https://apps.microsoft.com/detail/9NWPWZGRVL2C)

Click the button above to install **Instant Trace Viewer**.

### Winget (Command Line)

If you prefer using the command line, install **Instant Trace Viewer** with Windows Package Manager (`winget`):

```sh
winget install 9NWPWZGRVL2C
```

## Cloning the Repository

> ⚠️ **WARNING:** This repository uses nested submodules. Make sure you initialize submodules recursively, otherwise you will get compile errors!

To clone the repository, use the following command in your terminal:

```bash
git clone https://github.com/brycehutchings/InstantTraceViewer --recursive
```

If you forget to use `--recurse-submodules` when cloning, you can use the following command to update the submodules:

```bash
git submodule update --init --recursive
```

## Developer Builds

These are the latest builds produced by this project's GitHub Actions pipeline.

* Download [InstantTraceViewer-x64.zip](https://nightly.link/brycehutchings/InstantTraceViewer/workflows/build-windows/main/InstantTraceViewer-x64.zip)
* Download [InstantTraceViewer-ARM64.zip](https://nightly.link/brycehutchings/InstantTraceViewer/workflows/build-windows/main/InstantTraceViewer-ARM64.zip)
