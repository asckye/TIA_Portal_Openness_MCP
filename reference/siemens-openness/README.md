# TIA Portal AI Extensions

A collection of installable AI plugins for working with Siemens TIA Portal.
Each plugin has its own package directory and can be installed independently.

## Available plugins

- [TIA Portal Openness Development Kit](https://github.com/siemens/tia-portal-ai-extensions/blob/b5c7041648dc10f9225ef082306ade6c335b8771/openness_development/README.md) —
  32 Agent Skills for TIA Portal Openness.

## Install with GitHub Copilot CLI

Register this repository's marketplace, then install the plugin you need:

```shell
copilot plugin marketplace add siemens/tia-portal-ai-extensions
copilot plugin install tia-portal-openness-development-kit@tia-portal-ai-extensions
```

See the [Openness plugin documentation](https://github.com/siemens/tia-portal-ai-extensions/blob/b5c7041648dc10f9225ef082306ade6c335b8771/openness_development/README.md) for
compatibility details, prerequisites, and the complete skill list.

## Repository layout

This reference copy keeps the guide files under `skills/` for the MCP reader.
The upstream layout described below is not an installed plugin in this project.

- `.github/plugin/marketplace.json` lists the installable plugins.
- Each plugin package, such as `openness_development/`, contains its own
  manifest, documentation, and `skills/` directory.
