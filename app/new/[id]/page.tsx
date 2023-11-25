import { kv } from '@vercel/kv';
import { notFound } from 'next/navigation';
import { Metadata } from 'next';
import Body from '@/components/Body';

async function getAllKv(id: string) {
    const data = await kv.hgetall<{
        groupName: string;
        image?: string;
        formattedNumbers?: string;
        garbledText?: string;
        model_latency?: string;
    }>(id);

    return data;
}

export async function generateMetadata({
    params,
}: {
    params: {
        id: string;
    };
}): Promise<Metadata | undefined> {
    const data = await getAllKv(params.id);
    if (!data) {
        return;
    }

    const title = 'SMSer';
    const description = 'Group Texts, Easier';

    return {
        title,
        description,
        openGraph: {
            title,
            description,
            images: [],
        },
        twitter: {
            card: 'summary_large_image',
            title,
            description,
            images: [],
            creator: '@nickhudacin',
        },
    };
}

export default async function Results({
    params,
}: {
    params: {
        id: string;
    };
}) {
    const data = await getAllKv(params.id);
    if (!data) {
        notFound();
    }
    return (
        <Body
            groupName={data.groupName}
            image={data.image}
            formattedNumbers={data.formattedNumbers}
            garbledText={data.garbledText}
            modelLatency={Number(data.model_latency)}
            id={params.id}
        />
    );
}
