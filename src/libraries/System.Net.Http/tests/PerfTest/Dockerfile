ARG SDK_BASE_IMAGE=mcr.microsoft.com/dotnet/core/sdk:3.1.100-bionic
FROM $SDK_BASE_IMAGE

WORKDIR /app
COPY . .

ARG CONFIGURATION=Release
RUN dotnet build -c $CONFIGURATION

EXPOSE 5001

ENV CONFIGURATION=$CONFIGURATION
ENV HTTPSTRESS_ARGS='-runMode server -trace console -serverUri http://*:5001'
CMD ./run.sh
