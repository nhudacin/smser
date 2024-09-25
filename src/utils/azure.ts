const { DefaultAzureCredential } = require("@azure/identity");
const { BlobServiceClient } = require("@azure/storage-blob");


// need to obviously move this off local host
const connString = process.env.BLOB_STORAGE_CONNECTION;
const blobServiceClient = BlobServiceClient.fromConnectionString(connString);
const containerClient = blobServiceClient.getContainerClient("smser");

export async function uploadToContainer(id: String, data: String) {
    console.log("Ensuring Container Exists...");
    await containerClient.createIfNotExists();

    const blockBlobClient = containerClient.getBlockBlobClient(id);
    await blockBlobClient.upload(data, data.length);

    console.log(`Successfully Wrote`, id);
}

export async function getSMSData(id: String) {
    const blobClient = containerClient.getBlobClient(id);
    const downloadBlockBlobResponse = await blobClient.download();

    const downloaded = (
        await streamToBuffer(downloadBlockBlobResponse.readableStreamBody)
    ).toString();

    return downloaded;
}

async function streamToBuffer(readableStream: any) {
    return new Promise((resolve, reject) => {
        const chunks = [];
        readableStream.on("data", (data) => {
            chunks.push(data instanceof Buffer ? data : Buffer.from(data));
        });
        readableStream.on("end", () => {
            resolve(Buffer.concat(chunks));
        });
        readableStream.on("error", reject);
    });
}