ConditionNotExists Feature Test
================================
This installer places the main app under INSTALLDIR and writes a default
config file to AppData only on the FIRST install.

On upgrade the config file is never overwritten, preserving any user edits.
