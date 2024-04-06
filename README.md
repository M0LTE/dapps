# dapps

## Installing .NET 8 on Raspberry Pi OS (32 bit Buster)

```
wget https://download.visualstudio.microsoft.com/download/pr/61815861-c922-4462-a937-f6929747f0c2/7280600442a58ce080cd3d1494eca08f/dotnet-sdk-8.0.203-linux-arm.tar.gz
export DOTNET_ROOT=/opt/dotnet
sudo mkdir -p "$DOTNET_ROOT"
sudo tar zxf dotnet-sdk-8.0.203-linux-arm.tar.gz -C "$DOTNET_ROOT"
sudo sh -c 'echo "export DOTNET_ROOT=/opt/dotnet\nexport PATH=\$PATH:\$DOTNET_ROOT:\$DOTNET_ROOT/tools" >> /etc/profile'
export DOTNET_ROOT=/opt/dotnet
export PATH=$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools
```
