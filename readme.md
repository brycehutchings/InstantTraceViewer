﻿# Instant Trace Viewer

[![Windows Build](https://github.com/brycehutchings/InstantTraceViewer/actions/workflows/build-windows.yml/badge.svg)](https://github.com/brycehutchings/InstantTraceViewer/actions/workflows/build-windows.yml)

Instant Trace Viewer is a developer tool working with trace collection. It provides an easy-to-use interface for real-time Event Tracing for Windows (ETW) viewing as well as opening ETL files. The tool includes filtering options and graphical visualizations with the goal of making it effortless to see your program's trace logging as you develop software.

![image](https://github.com/user-attachments/assets/129b203a-be43-4366-8dde-1eb98eebbbaa)

## Cloning the Repository

> ⚠️ **WARNING:** This repository uses nested submodules. Make sure you initialize submodules recursively, otherwise you will get compile errors!

To clone the repository, use the following command in your terminal:

```bash
git clone --recurse-submodules https://github.com/brycehutchings/InstantTraceViewer
```

If you forget to use `--recurse-submodules` when cloning, you can use the following command to update the submodules:

```bash
git submodule update --init --recursive
```
