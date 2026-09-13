echo "Files in /c/Users/$USERNAME/AppData/Local/EVA/:"
find /c/Users/$USERNAME/AppData/Local/EVA/ -type f -printf "%P\n";

echo 
echo "Credential manager listing:"
cmdkey /list:EVA-EncryptionPassword
echo "done"
