# SMSer
-------------


## Pre-reqs

* Node (20.16.0)
* Docker
* Psake (`Install-Module Psake`)
* Azurite (`npm install -g azurite`)


## Run Locally

`.\build.ps1 RunDev`

## Docker Stuff

```
docker build . -t smser

# if using container registery
docker tag smser $registryName.azurecr.io/smser
az login
az acr login --name $registryName
docker push $registryName.azurecr.io/smser

```

```
docker run -p 3000:3000 smser
```