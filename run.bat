start http://localhost:8384
wsl docker run -p 8384:8384 -v ./db:/app/db -v /mnt/c/workspaces:/workspaces -e Workspace__RootPath=/workspaces jandini/graymoon:latest
